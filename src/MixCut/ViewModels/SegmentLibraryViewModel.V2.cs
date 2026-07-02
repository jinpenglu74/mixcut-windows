using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Models;
using MixCut.ViewModels.Cards;

namespace MixCut.ViewModels;

/// <summary>
/// SegmentLibraryViewModel V2 扩展 —— 给 MVVM 数据驱动视图（SegmentLibraryViewV2）用的字段和方法。
/// 通过 partial 隔离，不影响 V1 视图。
/// 实现 <see cref="ISegmentCardHost"/>，CardVM 反向调用入口。
/// </summary>
public partial class SegmentLibraryViewModel : ISegmentCardHost
{
    /// <summary>V2 视图绑定的分组列表（视频维度分组）。保留供老逻辑兼容，新视图用 Cards。</summary>
    public ObservableCollection<VideoGroupViewModel> Groups { get; } = new();

    /// <summary>V2 视图绑定的平铺卡片列表（用 VirtualizingWrapPanel 渲染）。</summary>
    public ObservableCollection<SegmentCardViewModel> Cards { get; } = new();

    /// <summary>所有卡片 VM 的快速索引（按 Segment.Id）。</summary>
    private readonly Dictionary<Guid, SegmentCardViewModel> _cardIndex = new();

    /// <summary>V1 时代用 SelectedSegment 表示单选；V2 同步推送 IsSelected 给 CardVM。</summary>
    private SegmentCardViewModel? _selectedCard;

    /// <summary>
    /// 分镜结构性变更（右键单删 / Ctrl+Z 撤销恢复）后触发，让 View 刷新统计 / 类型 chip / 空态。
    /// 对齐 CLAUDE.md §F「数据变更 → UI 刷新通道」：VM 改了 segments 必须广播给 View，否则统计数字停在旧值。
    /// （批量删除走 View 自己的 RefreshAfterSegmentChange；右键单删由 CardVM 命令触发，View 不在调用链上，故需此事件。）
    /// </summary>
    public event Action? SegmentsStructurallyChanged;

    // ============ V2 数据装载 ============

    /// <summary>切项目 / 筛选 / 排序变化时调用，重建 Groups。</summary>
    public void RebuildGroups()
    {
        // 计算筛选后的分镜（FilteredSegments 已经按现有 ApplyFilter 维护）
        var filtered = FilteredSegments.ToList();

        // 按 Video 分组（保留视频出现顺序）
        var groupsBuilt = new List<(Video Video, List<Segment> Segs)>();
        var videoIndex = new Dictionary<Guid, int>();
        foreach (var seg in filtered)
        {
            if (seg.Video is null) continue;
            if (!videoIndex.TryGetValue(seg.Video.Id, out var idx))
            {
                videoIndex[seg.Video.Id] = groupsBuilt.Count;
                groupsBuilt.Add((seg.Video, new List<Segment> { seg }));
            }
            else
            {
                groupsBuilt[idx].Segs.Add(seg);
            }
        }

        // 复用现有 CardVM；创建新分组容器
        var newGroups = new List<VideoGroupViewModel>(groupsBuilt.Count);
        var newCardIds = new HashSet<Guid>();
        foreach (var (video, segs) in groupsBuilt)
        {
            var cards = new List<SegmentCardViewModel>(segs.Count);
            foreach (var seg in segs)
            {
                newCardIds.Add(seg.Id);
                if (!_cardIndex.TryGetValue(seg.Id, out var card))
                {
                    card = new SegmentCardViewModel(seg, this);
                    _cardIndex[seg.Id] = card;
                }
                else
                {
                    card.RefreshFromSegment();
                }
                // 同步多选 + 序号
                card.IsSelectionMode = IsSelectionMode;
                card.IsChecked = SelectedSegmentIds.Contains(seg.Id);
                card.SequenceNumber = NumberFor(seg);
                cards.Add(card);
            }
            newGroups.Add(new VideoGroupViewModel(video, cards, _dubbing));
        }

        // 清理已不需要的 CardVM
        var toRemove = _cardIndex.Keys.Where(id => !newCardIds.Contains(id)).ToList();
        foreach (var id in toRemove)
        {
            if (_cardIndex.TryGetValue(id, out var c)) c.Dispose();
            _cardIndex.Remove(id);
        }

        // 用增量更新代替整体替换（避免 UI 全量重建）
        ReplaceGroupsInPlace(newGroups);

        var totalCards = newGroups.Sum(g => g.Segments.Count);
        Serilog.Log.Information(
            "[GroupDiag] groups={GroupCount} totalCards={Total} sortByQuality={SQ}",
            newGroups.Count, totalCards, SortByQuality);
    }

    private void ReplaceGroupsInPlace(List<VideoGroupViewModel> newGroups)
    {
        Groups.Clear();
        foreach (var g in newGroups)
        {
            Groups.Add(g);
        }
    }

    /// <summary>
    /// 平铺重建 Cards（用于 VirtualizingWrapPanel 渲染）。
    /// 视口外的 container 不会被实例化，1000 张分镜也能瞬时加载。
    /// </summary>
    public void RebuildCards()
    {
        var filtered = FilteredSegments.ToList();

        // 清掉过时 CardVM
        var newCardIds = new HashSet<Guid>(filtered.Select(s => s.Id));
        var toRemove = _cardIndex.Keys.Where(id => !newCardIds.Contains(id)).ToList();
        foreach (var id in toRemove)
        {
            if (_cardIndex.TryGetValue(id, out var c)) c.Dispose();
            _cardIndex.Remove(id);
        }

        // 直接整体替换（VirtualizingWrapPanel 会只渲染视口内的）
        Cards.Clear();
        foreach (var seg in filtered)
        {
            if (!_cardIndex.TryGetValue(seg.Id, out var card))
            {
                card = new SegmentCardViewModel(seg, this);
                _cardIndex[seg.Id] = card;
            }
            else
            {
                card.RefreshFromSegment();
            }
            card.IsSelectionMode = IsSelectionMode;
            card.IsChecked = SelectedSegmentIds.Contains(seg.Id);
            card.SequenceNumber = NumberFor(seg);
            Cards.Add(card);
        }
    }

    /// <summary>
    /// 异步分批装载 Groups：每加一组 yield 一帧，让 WPF 在每组之间完成 measure/arrange/render。
    /// 用户感知为"渐进出现"而不是"卡顿 1 秒"。
    /// </summary>
    public async Task RebuildGroupsAsync()
    {
        var filtered = FilteredSegments.ToList();

        var groupsBuilt = new List<(Video Video, List<Segment> Segs)>();
        var videoIndex = new Dictionary<Guid, int>();
        foreach (var seg in filtered)
        {
            if (seg.Video is null) continue;
            if (!videoIndex.TryGetValue(seg.Video.Id, out var idx))
            {
                videoIndex[seg.Video.Id] = groupsBuilt.Count;
                groupsBuilt.Add((seg.Video, new List<Segment> { seg }));
            }
            else
            {
                groupsBuilt[idx].Segs.Add(seg);
            }
        }

        // 清掉过时 CardVM
        var newCardIds = new HashSet<Guid>(filtered.Select(s => s.Id));
        var toRemove = _cardIndex.Keys.Where(id => !newCardIds.Contains(id)).ToList();
        foreach (var id in toRemove)
        {
            if (_cardIndex.TryGetValue(id, out var c)) c.Dispose();
            _cardIndex.Remove(id);
        }

        // 立即清空 Groups，UI 看到"已切到分镜库 + 加载中"
        Groups.Clear();
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);

        // 逐组异步 Add，每加完一组 yield 让 WPF 渲染该组
        foreach (var (video, segs) in groupsBuilt)
        {
            var cards = new List<SegmentCardViewModel>(segs.Count);
            foreach (var seg in segs)
            {
                if (!_cardIndex.TryGetValue(seg.Id, out var card))
                {
                    card = new SegmentCardViewModel(seg, this);
                    _cardIndex[seg.Id] = card;
                }
                else
                {
                    card.RefreshFromSegment();
                }
                card.IsSelectionMode = IsSelectionMode;
                card.IsChecked = SelectedSegmentIds.Contains(seg.Id);
                card.SequenceNumber = NumberFor(seg);
                cards.Add(card);
            }
            var group = new VideoGroupViewModel(video, cards, _dubbing);
            Groups.Add(group);

            // 让 WPF 渲染该组卡片（Background 优先级 = 等渲染周期完）
            await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>同步多选模式状态到所有 CardVM。</summary>
    public void SyncSelectionModeToCards()
    {
        foreach (var card in _cardIndex.Values)
        {
            card.IsSelectionMode = IsSelectionMode;
            if (!IsSelectionMode)
            {
                card.IsChecked = false;
            }
        }
    }

    /// <summary>同步 SelectedSegmentIds 到 CardVM.IsChecked。</summary>
    public void SyncCheckedToCards()
    {
        foreach (var card in _cardIndex.Values)
        {
            card.IsChecked = SelectedSegmentIds.Contains(card.Id);
        }
    }

    // ============ ISegmentCardHost 实现（走 V1 已有路径，含 ReExtractText 台词联动） ============

    void ISegmentCardHost.AdjustStartTime(SegmentCardViewModel card, double step)
    {
        AdjustStartFrame(card.Segment, Math.Sign(step));
        RefreshCardsForVideo(card.Segment.VideoId);
        ShowBoundaryFrame(card, isStart: true);
    }

    void ISegmentCardHost.AdjustEndTime(SegmentCardViewModel card, double step)
    {
        AdjustEndFrame(card.Segment, Math.Sign(step));
        RefreshCardsForVideo(card.Segment.VideoId);
        ShowBoundaryFrame(card, isStart: false);
    }

    void ISegmentCardHost.SetStartTime(SegmentCardViewModel card, double newStart)
    {
        SetStartTime(card.Segment, newStart);
        RefreshCardsForVideo(card.Segment.VideoId);
        ShowBoundaryFrame(card, isStart: true);
    }

    void ISegmentCardHost.SetEndTime(SegmentCardViewModel card, double newEnd)
    {
        SetEndTime(card.Segment, newEnd);
        RefreshCardsForVideo(card.Segment.VideoId);
        ShowBoundaryFrame(card, isStart: false);
    }

    private void RefreshCardsForVideo(Guid? videoId)
    {
        foreach (var card in _cardIndex.Values.Where(c => c.Segment.VideoId == videoId))
        {
            card.RefreshFromSegment();
        }
    }

    // ============ 剪映式逐帧边界预览 ============

    /// <summary>已解码边界帧缓存（key=视频路径|帧号），来回微调同一帧立即命中、不重复抽帧。</summary>
    private readonly Dictionary<string, System.Windows.Media.ImageSource> _scrubFrameCache = new();

    /// <summary>每张卡的最新抽帧请求序号：连点微调时只让最后一次结果落地，避免旧帧覆盖新帧。</summary>
    private readonly Dictionary<Guid, int> _scrubToken = new();

    private string? _scrubDir;

    /// <summary>
    /// 调 IN/OUT 后把卡片预览切到「当前调到的那一帧」：调 IN 显示新起始帧、调 OUT 显示新末帧
    /// （EndFrame 不含，末画面是 EndFrame-1）。对齐剪映：边界走到哪一帧，预览就显示哪一帧，逐帧跟随。
    /// 抽帧用自带 ffmpeg 输入级 seek（与缩略图同源），带缓存 + 最新优先，连点也不乱。
    /// </summary>
    private async void ShowBoundaryFrame(SegmentCardViewModel card, bool isStart)
    {
        try
        {
            if (_ffmpeg is null) return;
            var seg = card.Segment;
            var path = seg.Video?.LocalPath;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            var fps = seg.EffectiveFps;
            if (fps <= 0) return;

            var frame = isStart
                ? Math.Max(0, seg.StartFrame)
                : Utilities.FrameTime.LastIncludedFrame(seg.StartFrame, seg.EndFrame);
            var timeSec = Utilities.FrameTime.FrameToSeconds(frame, fps);

            // 最新优先：本卡每次请求递增序号，await 回来后若已不是最新就丢弃（连点时只认最后一帧）。
            var token = (_scrubToken.TryGetValue(seg.Id, out var t) ? t : 0) + 1;
            _scrubToken[seg.Id] = token;

            var key = path + "|" + frame.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_scrubFrameCache.TryGetValue(key, out var cached))
            {
                card.ScrubImage = cached;
                return;
            }

            var outPath = System.IO.Path.Combine(
                ScrubDir(),
                Math.Abs(path.GetHashCode()).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "_" + frame.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jpg");
            if (!System.IO.File.Exists(outPath))
            {
                await _ffmpeg.GenerateThumbnailAsync(path, outPath, timeSec);
            }
            if (!System.IO.File.Exists(outPath)) return;

            var img = LoadFrameBitmap(outPath);
            if (img is null) return;

            // 软上限：避免长时间微调把内存缓存撑爆。
            if (_scrubFrameCache.Count > 240) _scrubFrameCache.Clear();
            _scrubFrameCache[key] = img;

            // 过期请求不覆盖（用户已经又点了好几下）。
            if (_scrubToken.TryGetValue(seg.Id, out var cur) && cur == token)
            {
                card.ScrubImage = img;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[ScrubDiag] 抽边界帧失败");
        }
    }

    private string ScrubDir()
    {
        if (_scrubDir is null)
        {
            _scrubDir = System.IO.Path.Combine(Utilities.AppPaths.Root, "ScrubCache");
            System.IO.Directory.CreateDirectory(_scrubDir);
        }
        return _scrubDir;
    }

    private static System.Windows.Media.ImageSource? LoadFrameBitmap(string path)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 540; // 按显示尺寸解码，省内存（对齐 ThumbnailCache）
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze(); // 冻结后可跨线程安全赋给 UI 绑定
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    void ISegmentCardHost.RequestDelete(SegmentCardViewModel card)
    {
        var result = MessageBox.Show(
            $"删除分镜 {card.SegmentIndexLabel}？删除后可按 Ctrl+Z 撤销。",
            "确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        // P0-10 修：右键单删此前直接删、不入撤销栈，导致删完按 Ctrl+Z 提示「没有可撤销的操作」。
        // 现与批量删除路径对齐：删前快照 → 删除成功才压栈 → Ctrl+Z 调 RestoreSegments 恢复。
        var snapshot = Infrastructure.UndoStack.UndoClone.CloneSegment(card.Segment);
        if (!DeleteSegment(card.Segment))
        {
            // 不谎报成功、不压栈，给人话提示 + 重试入口（对齐批量删除的失败处理）。
            Views.Components.ToastService.Show("删除失败，请重试", Views.Components.ToastStyle.Error);
            return;
        }
        RebuildGroups();
        SegmentsStructurallyChanged?.Invoke();

        Infrastructure.UndoStack.UndoManager.Shared.Push(
            new Infrastructure.UndoStack.DelegateUndoAction(
                "删除 1 个分镜",
                () =>
                {
                    var n = RestoreSegments(new[] { snapshot });
                    RebuildGroups();
                    SegmentsStructurallyChanged?.Invoke();
                    Views.Components.ToastService.Show(
                        n > 0 ? "已恢复 1 个分镜" : "恢复失败，请重试",
                        n > 0 ? Views.Components.ToastStyle.Success : Views.Components.ToastStyle.Error);
                }));
        Views.Components.ToastService.Show("已删除分镜", Views.Components.ToastStyle.Warning,
            "撤销", () => Infrastructure.UndoStack.UndoManager.Shared.Undo());
    }

    void ISegmentCardHost.ToggleSemanticType(SegmentCardViewModel card, SemanticType type)
    {
        ToggleSemanticType(card.Segment, type);
        card.RefreshFromSegment();
    }

    void ISegmentCardHost.UpdatePositionType(SegmentCardViewModel card, PositionType type)
    {
        UpdatePositionType(card.Segment, type);
        card.RefreshFromSegment();
    }

    void ISegmentCardHost.ToggleSelection(SegmentCardViewModel card)
    {
        ToggleSelection(card.Segment);
        card.IsChecked = SelectedSegmentIds.Contains(card.Id);
    }

    void ISegmentCardHost.SelectCard(SegmentCardViewModel card)
    {
        if (_selectedCard is { } prev && prev != card)
        {
            prev.IsSelected = false;
        }
        card.IsSelected = true;
        _selectedCard = card;
        SelectedSegment = card.Segment;
        // v0.5.0：单击分镜 → 右侧配音变体检视器加载该分镜。
        _ = DubInspector?.LoadAsync(card.Segment);
    }

    // ============ P3：字幕处理（保留原声 / 字幕处理 / 遮挡框） ============

    async Task ISegmentCardHost.ToggleVoiceLockAsync(SegmentCardViewModel card, bool locked)
    {
        await _dubbing.SetVoiceLockedAsync(card.Segment.Id, locked);
        // 关键修复：_dubbing 用独立短上下文改的是 DB 里另一个 Segment 实例，card.Segment 是内存里
        // LoadSegments 时加载的对象，不会被那次写入更新。必须手动同步内存对象，否则 RefreshFromSegment
        // 读到旧值 → 复选框/字幕处理面板不刷新 → 用户感知「点了没反应」。
        card.Segment.IsVoiceLocked = locked;
        // 刷新该卡 + 检视器（锁定 → 检视器切到 Locked 态）
        // DubsChanged 已由 SetVoiceLockedAsync 内部触发（刷新设置条变体计数）
        card.RefreshFromSegment();
        _ = DubInspector?.LoadAsync(card.Segment);
    }

    async Task ISegmentCardHost.SetSubtitleTreatmentAsync(SegmentCardViewModel card, SubtitleTreatment treatment)
    {
        await _dubbing.SetSubtitleTreatmentAsync(card.Segment.Id, treatment);
        // 同上：同步内存 Segment，否则三胶囊高亮不切换（DataTrigger 绑 SubtitleTreatment 读到旧值）。
        card.Segment.SubtitleTreatment = treatment;
        // DubsChanged 已由 SetSubtitleTreatmentAsync 内部触发
        card.RefreshFromSegment();
    }

    void ISegmentCardHost.CommitMaskRect(SegmentCardViewModel card)
    {
        _ = _dubbing.SetMaskRectAsync(card.Segment.Id, card.MaskRect);
    }

    async Task ISegmentCardHost.ApplyMaskToAllAsync(SegmentCardViewModel card)
    {
        var n = await _dubbing.ApplyMaskToAllAsync(card.Segment.Id);

        // _dubbing 走的是另一个短上下文，只改了 DB —— 分镜库 VM 的 _segments（卡片正绑定的对象）还是旧值。
        // 必须把源分镜的「字幕处理 + 遮挡框」同步到同一视频其余分镜的内存对象，再刷新对应卡片，UI 才联动。
        // 不能走 LoadSegments()：它换成新实例后 RebuildGroups 复用的旧卡片仍指向旧 Segment 对象 → RefreshFromSegment 读到旧值（正是本 bug 根因）。
        var src = card.Segment;
        var videoId = src.VideoId;
        foreach (var seg in _segments.Where(s => s.VideoId == videoId && s.Id != src.Id))
        {
            seg.HasHardSubtitle = src.HasHardSubtitle;
            seg.MaskStyleRaw = src.MaskStyleRaw;
            seg.MaskRectJson = src.MaskRectJson;
        }
        // 刷新受影响卡片：字幕处理胶囊高亮 / 遮挡框显隐 / 所见即所得预览都要跟着变（含源卡自身）。
        foreach (var c in _cardIndex.Values.Where(c => c.Segment.VideoId == videoId))
        {
            c.RefreshFromSegment();
        }
        Serilog.Log.Information("[GroupDiag] 遮挡应用到所有 src={Seg} affected={N} 刷新卡片={Cards}",
            src.Id, n, _cardIndex.Values.Count(c => c.Segment.VideoId == videoId));

        Views.Components.ToastService.Show(
            n > 0 ? $"已将字幕处理应用到本视频其余 {n} 个分镜" : "本视频没有其它分镜",
            Views.Components.ToastStyle.Success);
    }

    void ISegmentCardHost.RefreshCardFromHost(SegmentCardViewModel card) => card.RefreshFromSegment();

    async Task<string> ISegmentCardHost.ReRecognizeSegmentAsync(SegmentCardViewModel card, CancellationToken ct)
    {
        try
        {
            var newText = await _dubbing.ReRecognizeSegmentAsync(card.Segment.Id, ct);
            // 同步内存 Segment.Text：ReRecognizeSegmentAsync 只改了另一个短上下文里的实体，
            // 不同步这里的话，卡片随后 RefreshFromSegment 会用旧 _segment.Text 把新识别结果覆盖回去，
            // 看着像「点了没反应」（对齐 [[dub-p3-inmemory-sync-bug]] 教训）。
            card.Segment.Text = newText;
            Views.Components.ToastService.Show(
                $"重识别完成（{newText.Length} 字）",
                Views.Components.ToastStyle.Success);
            return newText;
        }
        catch (Exception ex)
        {
            var msg = ex is MixCut.Services.Dubbing.DubException d ? d.Message : "重识别失败，请稍后重试";
            Views.Components.ToastService.Show(msg, Views.Components.ToastStyle.Error);
            return string.Empty;
        }
    }

    async Task<string> ISegmentCardHost.SaveSegmentTextAsync(SegmentCardViewModel card, string newText, CancellationToken ct)
    {
        var saved = await _dubbing.UpdateSegmentTextAsync(card.Segment.Id, newText, ct);
        // 同步内存 Segment.Text（同 ReRecognize：短上下文改的是另一个实例）。
        card.Segment.Text = saved;
        Views.Components.ToastService.Show("台词已保存", Views.Components.ToastStyle.Success);
        return saved;
    }

}
