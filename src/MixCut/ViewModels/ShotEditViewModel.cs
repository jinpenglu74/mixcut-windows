using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Models;
using MixCut.Services.ShotEdit;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.ViewModels;

/// <summary>
/// 分镜头 AI 画面替换工作区 VM（对应 macOS ShotEditViewModel）。
/// 承载：切分/加载物理镜头、帧级编辑（合并/拆分/移边界）、AI 变体生成/删除、占位选择、合成就地替换。
/// 所有 DB 写走短生命周期 <see cref="MixCutDbContext"/>；display 集合是 AsNoTracking 脱离态副本，操作后重载。
/// </summary>
public sealed class ShotEditViewModel
{
    private readonly IDbContextFactory<MixCutDbContext> _dbFactory;
    private readonly ShotSlicerService _slicer;
    private readonly ShotVariantService _variantService;
    private readonly ShotCompositionService _composition;
    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<ShotEditViewModel> _logger;

    public ShotEditViewModel(
        IDbContextFactory<MixCutDbContext> dbFactory, ShotSlicerService slicer,
        ShotVariantService variantService, ShotCompositionService composition,
        FFmpegRunner ffmpeg, ILogger<ShotEditViewModel> logger)
    {
        _dbFactory = dbFactory;
        _slicer = slicer;
        _variantService = variantService;
        _composition = composition;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    // ---- 上下文 ----
    public Guid SegmentId { get; private set; }
    public string SourceVideoPath { get; private set; } = string.Empty;
    public string VideoHash { get; private set; } = string.Empty;
    public double Fps { get; private set; }
    public double SegmentStart { get; private set; }
    public double SegmentEnd { get; private set; }
    public string SegmentCaption { get; private set; } = string.Empty;

    // ---- 状态 ----
    /// <summary>当前镜头（orderIndex 升序，AsNoTracking 副本，含 Variants）。</summary>
    public List<PhysicalShot> Shots { get; private set; } = new();
    /// <summary>占位选择：orderIndex → 变体 id（null = 原版）。</summary>
    public Dictionary<int, Guid?> Selections { get; } = new();
    /// <summary>正在生成的变体 id（防重入 + 转圈）。</summary>
    public HashSet<Guid> BusyVariantIds { get; } = new();
    public bool IsSlicing { get; private set; }
    public bool IsComposing { get; private set; }
    /// <summary>合成阶段文案（切片 i/N → 拼接 → 混音），供合成按钮实时显示进度。</summary>
    public string ComposeStatus { get; private set; } = "合成中…";
    public string? ErrorMessage { get; private set; }
    public int SelectedOrderIndex { get; set; } = 1;

    /// <summary>状态变更（切分/编辑/生成/删除后）→ UI 重建。</summary>
    public event Action? Changed;
    /// <summary>某变体生成状态文案更新（variantId, 文案）。</summary>
    public event Action<Guid, string>? VariantProgress;

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>合成完整性：镜头数 &gt; 0 且 1..N 每坑都有选择。</summary>
    public bool CanCompose => ShotEditRules.CanCompose(Shots.Count, Selections);

    // ---- 加载 / 切分 ----

    /// <summary>加载某分镜的物理镜头；未切过则按场景检测切分并落库。对应 mac loadShots。</summary>
    public async Task LoadShotsAsync(Segment segment, CancellationToken ct = default)
    {
        SegmentId = segment.Id;
        SegmentStart = segment.StartTime;
        SegmentEnd = segment.EndTime;
        SegmentCaption = string.IsNullOrEmpty(segment.Text) ? "无台词" : segment.Text;
        var video = segment.Video;
        SourceVideoPath = video?.LocalPath ?? string.Empty;
        VideoHash = video?.ContentHash ?? string.Empty;
        Fps = segment.EffectiveFps;
        ErrorMessage = null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.PhysicalShots.AsNoTracking()
            .Include(s => s.Variants)
            .Where(s => s.SegmentId == segment.Id)
            .OrderBy(s => s.OrderIndex)
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            Shots = existing;                          // 已切过 → 直接用，不重切
        }
        else
        {
            if (string.IsNullOrEmpty(SourceVideoPath) || Fps <= 0)
            {
                ErrorMessage = "该分镜缺少视频 / 帧率信息，无法切分镜头";
                Shots = new List<PhysicalShot>();
                RaiseChanged();
                return;
            }
            IsSlicing = true;
            RaiseChanged();
            try
            {
                var ranges = await _slicer.ComputeShotsAsync(
                    SourceVideoPath, SegmentStart, SegmentEnd, Fps, ct: ct);
                foreach (var r in ranges)
                {
                    db.PhysicalShots.Add(new PhysicalShot
                    {
                        SegmentId = segment.Id,
                        OrderIndex = r.OrderIndex,
                        StartFrame = r.StartFrame,
                        EndFrame = r.EndFrame,
                    });
                }
                await db.SaveChangesAsync(ct);
            }
            finally { IsSlicing = false; }

            Shots = await db.PhysicalShots.AsNoTracking()
                .Include(s => s.Variants)
                .Where(s => s.SegmentId == segment.Id)
                .OrderBy(s => s.OrderIndex)
                .ToListAsync(ct);
        }

        LoadSelectionsFromShots();
        // 首批缩略图未就绪时保持「准备中」loading，避免露黑卡（用户反馈：加载时全黑没 loading）。
        // 复用 IsSlicing 作为整体「准备中」态覆盖切分 + 缩略图生成，全部就绪才揭开卡片。
        var needThumbs = Shots.Any(s =>
            string.IsNullOrEmpty(s.ThumbnailPath) || !System.IO.File.Exists(s.ThumbnailPath));
        IsSlicing = needThumbs;
        RaiseChanged();
        if (needThumbs)
        {
            await EnsureShotThumbnailsAsync(ct);
            IsSlicing = false;
            RaiseChanged();
        }
    }

    private void LoadSelectionsFromShots()
    {
        Selections.Clear();
        foreach (var shot in Shots)
        {
            Guid? choice = null;
            if (shot.SelectedVariantId is { } vid
                && shot.Variants.FirstOrDefault(v => v.Id == vid) is { } v && v.IsUsable)
            {
                choice = vid;
            }
            Selections[shot.OrderIndex] = choice;
        }
        if (!Selections.ContainsKey(SelectedOrderIndex) && Shots.Count > 0)
        {
            SelectedOrderIndex = Shots[0].OrderIndex;
        }
    }

    /// <summary>
    /// 给缺缩略图的镜头在 startFrame 处抽首帧（失败不阻塞）。R1 性能：ffmpeg 并发抽帧（gate=4），
    /// 12 镜头加载从串行 ~6s 降到 ~2s；抽帧完成后在单个 DbContext 上串行落库（DbContext 非线程安全）。
    /// </summary>
    private async Task EnsureShotThumbnailsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(SourceVideoPath) || Fps <= 0 || string.IsNullOrEmpty(VideoHash)) return;
        var pending = Shots.Where(s => string.IsNullOrEmpty(s.ThumbnailPath)).ToList();
        if (pending.Count == 0) return;

        var dir = AppPaths.ShotThumbnailsDirectory(VideoHash);
        var results = new System.Collections.Concurrent.ConcurrentDictionary<Guid, string>();
        var maxPar = Math.Min(4, Math.Max(2, Environment.ProcessorCount / 2));
        using var gate = new SemaphoreSlim(maxPar);
        await Task.WhenAll(pending.Select(async shot =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var thumb = Path.Combine(dir, $"{shot.Id:N}.jpg");
                var t = FrameTime.FrameToSeconds(shot.StartFrame, Fps);
                await _ffmpeg.GenerateThumbnailAsync(SourceVideoPath, thumb, t, ct);
                results[shot.Id] = thumb;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ShotEditDiag] 镜头缩略图生成失败 shot={Shot}", shot.Id);
            }
            finally { gate.Release(); }
        }));

        if (results.IsEmpty) return;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var shot in Shots)
        {
            if (!results.TryGetValue(shot.Id, out var thumb)) continue;
            var tracked = await db.PhysicalShots.FirstOrDefaultAsync(s => s.Id == shot.Id, ct);
            if (tracked is not null) tracked.ThumbnailPath = thumb;
            shot.ThumbnailPath = thumb;
        }
        await db.SaveChangesAsync(ct);
        RaiseChanged();
    }

    // ---- 编辑：合并 / 拆分 / 移边界 ----

    public Task MergeShotsAsync(int i, CancellationToken ct = default) =>
        ApplyPartitionAsync(ShotPartitionEditor.Merge(CurrentSpans(), i), ct);

    public Task SplitShotAsync(int orderIndex, CancellationToken ct = default)
    {
        var idx = Shots.FindIndex(s => s.OrderIndex == orderIndex);
        if (idx < 0) return Task.CompletedTask;
        return ApplyPartitionAsync(ShotPartitionEditor.SplitAtMidpoint(CurrentSpans(), idx), ct);
    }

    private IReadOnlyList<ShotSpan> CurrentSpans() =>
        Shots.Select(s => new ShotSpan(s.StartFrame, s.EndFrame)).ToList();

    /// <summary>把新的切分（改变镜头数）对账落库：按 (start,end) 相等保留身份，否则删/建；改动镜头变体随级联删。</summary>
    private async Task ApplyPartitionAsync(IReadOnlyList<ShotSpan> newSpans, CancellationToken ct)
    {
        if (newSpans.SequenceEqual(CurrentSpans())) return;   // no-op，不误清替换画面

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tracked = await db.PhysicalShots
            .Where(s => s.SegmentId == SegmentId)
            .OrderBy(s => s.OrderIndex)
            .ToListAsync(ct);

        var reused = new HashSet<Guid>();
        for (var idx = 0; idx < newSpans.Count; idx++)
        {
            var span = newSpans[idx];
            var match = tracked.FirstOrDefault(s =>
                s.StartFrame == span.StartFrame && s.EndFrame == span.EndFrame && !reused.Contains(s.Id));
            if (match is not null)
            {
                match.OrderIndex = idx + 1;
                reused.Add(match.Id);
            }
            else
            {
                db.PhysicalShots.Add(new PhysicalShot
                {
                    SegmentId = SegmentId,
                    OrderIndex = idx + 1,
                    StartFrame = span.StartFrame,
                    EndFrame = span.EndFrame,
                });
            }
        }
        // 未被复用的旧镜头 → 删除（变体随级联删）。
        foreach (var old in tracked.Where(s => !reused.Contains(s.Id)))
        {
            db.PhysicalShots.Remove(old);
        }

        await InvalidateReplacedPictureAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await ReloadShotsAsync(ct);
    }

    /// <summary>移动边界 ±帧（松手 / ±按钮一次性提交）。动过的两邻镜头变体全删、替换画面作废。</summary>
    public async Task NudgeBoundaryAsync(int boundaryIndex, int deltaFrames, CancellationToken ct = default)
    {
        var spans = CurrentSpans();
        var b = boundaryIndex;
        if (b < 0 || b + 1 >= spans.Count) return;
        var target = spans[b].EndFrame + deltaFrames;
        var newSpans = ShotPartitionEditor.MoveBoundary(spans, b, target);
        if (newSpans.SequenceEqual(spans)) return;   // 已到 clamp 边界，无变化

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tracked = await db.PhysicalShots
            .Include(s => s.Variants)
            .Where(s => s.SegmentId == SegmentId)
            .OrderBy(s => s.OrderIndex)
            .ToListAsync(ct);
        if (b + 1 >= tracked.Count) return;

        // 只动 b 与 b+1 两个镜头：改帧号 + 删这两个镜头的全部变体（连磁盘文件）+ 清选择/缩略图。
        foreach (var bi in new[] { b, b + 1 })
        {
            var shot = tracked[bi];
            shot.StartFrame = newSpans[bi].StartFrame;
            shot.EndFrame = newSpans[bi].EndFrame;
            foreach (var v in shot.Variants.ToList())
            {
                TryDelete(v.ResultVideoPath);
                TryDelete(v.ThumbnailPath);
                db.ShotVariants.Remove(v);
            }
            shot.SelectedVariantId = null;
            shot.ThumbnailPath = null;
        }

        await InvalidateReplacedPictureAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await ReloadShotsAsync(ct);
        await EnsureShotThumbnailsAsync(ct);
    }

    // ---- AI 变体 ----

    /// <summary>为某镜头生成 AI 画面变体。对应 mac generateVariant。</summary>
    public async Task GenerateVariantAsync(Guid shotId, string prompt, CancellationToken ct = default)
    {
        var p = prompt.Trim();
        if (p.Length == 0) return;
        var shot = Shots.FirstOrDefault(s => s.Id == shotId);
        if (shot is null) return;
        if (string.IsNullOrEmpty(SourceVideoPath) || Fps <= 0 || string.IsNullOrEmpty(VideoHash))
        {
            ErrorMessage = "缺少视频 / 帧率 / 哈希信息，无法生成变体";
            RaiseChanged();
            return;
        }
        var durSec = FrameTime.FrameToSeconds(shot.FrameCount, Fps);
        if (!ShotEditRules.IsEditable(durSec))
        {
            ErrorMessage = ShotEditRules.IneligibleReason(durSec);
            RaiseChanged();
            return;
        }

        var variantId = Guid.NewGuid();
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            db.ShotVariants.Add(new ShotVariant
            {
                Id = variantId, ShotId = shotId, Prompt = p,
                StatusRaw = nameof(ShotVariantStatus.Generating), CreatedAt = DateTime.Now,
            });
            await db.SaveChangesAsync(ct);
        }
        BusyVariantIds.Add(variantId);
        await ReloadShotsAsync(ct);
        VariantProgress?.Invoke(variantId, "生成中");

        try
        {
            var result = await _variantService.GenerateAsync(
                SourceVideoPath, VideoHash, shotId, variantId,
                shot.StartFrame, shot.EndFrame, Fps, p,
                status => VariantProgress?.Invoke(variantId, status), ct);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var v = await db.ShotVariants.FirstOrDefaultAsync(x => x.Id == variantId, ct);
            if (v is not null)
            {
                v.ResultVideoPath = result.ResultVideoPath;
                v.ThumbnailPath = string.IsNullOrEmpty(result.ThumbnailPath) ? null : result.ThumbnailPath;
                v.Status = ShotVariantStatus.Completed;
                await db.SaveChangesAsync(ct);
            }
            ErrorMessage = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var friendly = ex is ShotEditException ? ex.Message
                : "画面替换失败：" + MixCut.Services.AI.ApiErrorClassifier.Friendly(ex);
            await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
            var v = await db.ShotVariants.FirstOrDefaultAsync(x => x.Id == variantId, CancellationToken.None);
            if (v is not null)
            {
                v.Status = ShotVariantStatus.Failed;
                v.FriendlyError = friendly;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            ErrorMessage = friendly;
            _logger.LogError(ex, "[ShotEditDiag] 变体生成失败 variant={Variant}", variantId);
        }
        finally
        {
            BusyVariantIds.Remove(variantId);
            await ReloadShotsAsync(ct);
        }
    }

    /// <summary>删除某变体（连磁盘文件）；若被某坑选中则回退原版。</summary>
    public async Task DeleteVariantAsync(Guid variantId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var v = await db.ShotVariants.FirstOrDefaultAsync(x => x.Id == variantId, ct);
        if (v is null) return;
        TryDelete(v.ResultVideoPath);
        TryDelete(v.ThumbnailPath);
        // 若某镜头选中它 → 回退原版。
        var shots = await db.PhysicalShots.Where(s => s.SegmentId == SegmentId && s.SelectedVariantId == variantId).ToListAsync(ct);
        foreach (var s in shots) s.SelectedVariantId = null;
        db.ShotVariants.Remove(v);
        await db.SaveChangesAsync(ct);
        await ReloadShotsAsync(ct);
    }

    // ---- 占位选择 ----

    /// <summary>为某镜头选定版本（variantId=null → 原版），持久化到 PhysicalShot.SelectedVariantId。</summary>
    public async Task SelectAsync(int orderIndex, Guid? variantId, CancellationToken ct = default)
    {
        Selections[orderIndex] = variantId;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var shot = await db.PhysicalShots.FirstOrDefaultAsync(
            s => s.SegmentId == SegmentId && s.OrderIndex == orderIndex, ct);
        if (shot is not null)
        {
            shot.SelectedVariantId = variantId;
            await db.SaveChangesAsync(ct);
            var local = Shots.FirstOrDefault(s => s.OrderIndex == orderIndex);
            if (local is not null) local.SelectedVariantId = variantId;
        }
        RaiseChanged();
    }

    // ---- 合成就地替换 ----

    /// <summary>把各镜头选定版本合成为新画面，就地写回分镜「替换画面」字段。成功返回 true。对应 mac compose。</summary>
    public async Task<bool> ComposeAsync(CancellationToken ct = default)
    {
        if (!CanCompose)
        {
            ErrorMessage = "每个分镜头位置都要选一个版本";
            RaiseChanged();
            return false;
        }
        if (string.IsNullOrEmpty(SourceVideoPath) || Fps <= 0 || string.IsNullOrEmpty(VideoHash))
        {
            ErrorMessage = "缺少视频 / 帧率 / 哈希信息，无法合成";
            RaiseChanged();
            return false;
        }

        IsComposing = true;
        RaiseChanged();
        try
        {
            var slots = new List<ShotSlotInput>();
            foreach (var shot in Shots.OrderBy(s => s.OrderIndex))
            {
                string? variantPath = null;
                if (Selections.TryGetValue(shot.OrderIndex, out var vid) && vid is { } id)
                {
                    var v = shot.Variants.FirstOrDefault(x => x.Id == id);
                    if (v is { } vv && vv.IsUsable) variantPath = vv.ResultVideoPath;
                }
                slots.Add(new ShotSlotInput(shot.StartFrame, shot.EndFrame, variantPath));
            }

            ComposeStatus = "合成中…";
            var result = await _composition.ComposeAsync(
                SourceVideoPath, Fps, slots, SegmentStart, SegmentEnd,
                phase => { ComposeStatus = phase; RaiseChanged(); }, ct);

            // 落地到 ReplacedPictures 目录。
            var dir = AppPaths.ReplacedPicturesDirectory(VideoHash);
            var destPath = Path.Combine(dir, $"{SegmentId:N}.mp4");
            var thumbPath = Path.Combine(dir, $"{SegmentId:N}.jpg");
            if (File.Exists(destPath)) TryDelete(destPath);
            File.Move(result.CompositeVideoPath, destPath);
            try { await _ffmpeg.GenerateThumbnailAsync(destPath, thumbPath, 0.0, ct); }
            catch { thumbPath = string.Empty; }

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == SegmentId, ct);
            if (seg is null) { ErrorMessage = "分镜已不存在"; return false; }
            seg.ReplacedPictureVideoPath = destPath;
            seg.ReplacedPictureThumbnailPath = string.IsNullOrEmpty(thumbPath) ? null : thumbPath;
            seg.ReplacedPictureFrameCount = result.TotalFrames;
            seg.PictureShowsReplaced = true;
            await db.SaveChangesAsync(ct);
            ErrorMessage = null;
            _logger.LogInformation("[ShotEditDiag] 合成就地替换完成 seg={Seg} frames={Frames}", SegmentId, result.TotalFrames);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorMessage = ex is ShotEditException ? ex.Message : "合成失败：" + ex.Message;
            _logger.LogError(ex, "[ShotEditDiag] 合成失败 seg={Seg}", SegmentId);
            return false;
        }
        finally
        {
            IsComposing = false;
            RaiseChanged();
        }
    }

    // ---- 工具 ----

    private async Task ReloadShotsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        Shots = await db.PhysicalShots.AsNoTracking()
            .Include(s => s.Variants)
            .Where(s => s.SegmentId == SegmentId)
            .OrderBy(s => s.OrderIndex)
            .ToListAsync(ct);
        LoadSelectionsFromShots();
        RaiseChanged();
    }

    private async Task InvalidateReplacedPictureAsync(MixCutDbContext db, CancellationToken ct)
    {
        var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == SegmentId, ct);
        seg?.InvalidateReplacedPicture();
    }

    /// <summary>镜头时长（秒）。</summary>
    public double DurationOf(PhysicalShot shot) => FrameTime.FrameToSeconds(shot.FrameCount, Fps);

    /// <summary>该镜头能否生成 AI 变体（时长 ∈ [2s,10s]）。</summary>
    public bool IsEditable(PhysicalShot shot) => ShotEditRules.IsEditable(DurationOf(shot));

    /// <summary>不可编辑原因（超限）。</summary>
    public string? IneligibleReason(PhysicalShot shot) => ShotEditRules.IneligibleReason(DurationOf(shot));

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }
}
