using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using MixCut.Models;
using MixCut.ViewModels;
using MixCut.ViewModels.Cards;

namespace MixCut.Views;

/// <summary>
/// SegmentLibraryView V2 —— MVVM 数据驱动版。
/// 关键设计：DataTemplate 默认轻量（只放 Image + 占位），hover 才动态挂 InlineVideoPlayer。
/// 这样 100 张卡片不会有 100 个 MediaElement 同时存在。
/// </summary>
public partial class SegmentLibraryViewV2 : UserControl, IProjectView
{
    private readonly SegmentLibraryViewModel _vm;
    private readonly Services.Export.VariantBatchExportService _variantExport;
    private readonly Utilities.AppSettings _settings;
    private readonly IServiceProvider _services;
    private Project? _currentProject;

    public SegmentLibraryViewV2(
        SegmentLibraryViewModel vm,
        Services.Export.VariantBatchExportService variantExport,
        Utilities.AppSettings settings,
        IServiceProvider services)
    {
        _vm = vm;
        _variantExport = variantExport;
        _settings = settings;
        _services = services;
        InitializeComponent();
        DataContext = _vm;

        BuildTypeChips();
        _vm.SelectionChanged += OnSelectionChanged;
        // 右键单删 / Ctrl+Z 恢复后，VM 广播此事件，View 刷新统计 / 类型 chip / 空态（VM 已自行 RebuildGroups）。
        _vm.SegmentsStructurallyChanged += OnSegmentsStructurallyChanged;
        // 调 IN/OUT 帧后播放境界窗口（对齐 Mac，见 OnBoundaryPreviewRequested）。
        _vm.BoundaryPreviewRequested += OnBoundaryPreviewRequested;
        // #12：卡片右键「分镜头替换」→ VM 抛事件 → 这里开工作区窗口。
        _vm.ShotEditRequested += OnShotEditRequested;

        Focusable = true;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>#12：打开「分镜头替换」全屏工作区；关闭后把替换画面同步回卡片。</summary>
    private async void OnShotEditRequested(Segment segment)
    {
        try
        {
            var vm = _services.GetRequiredService<ShotEditViewModel>();
            var win = new ShotEditWindow(vm, segment) { Owner = Window.GetWindow(this) };
            win.ShowDialog();
            await _vm.ReloadReplacedPictureAsync(segment);
        }
        catch (Exception ex)
        {
            Components.ToastService.Show(
                "打开分镜头替换失败：" + ex.Message, Components.ToastStyle.Error);
        }
    }

    public void LoadProject(Project project)
    {
        if (_currentProject?.Id == project.Id) return;
        _currentProject = project;

        _vm.SetSelectionMode(false);
        _vm.ClearSelection();
        _vm.LoadSegments(project);
        BuildTypeChips();
        // 卡片立即渲染（黑底先出，CardVM 后台异步加载缩略图，加载完 INPC 刷新）
        _vm.RebuildGroups();
        UpdateStats();
        UpdateSelectionToolbar();
        UpdateEmptyState();
        // 已有 thumbnail 立即走 cache
        var paths = _vm.FilteredSegments
            .Select(s => s.ThumbnailPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
        foreach (var p in paths)
        {
            _ = Infrastructure.ThumbnailCache.Shared.LoadAsync(p);
        }

        // 后台补生成缺失的 segment thumbnail（历史数据修复）
        _ = _vm.RepairMissingThumbnailsAsync();

        // 自验证：3 秒后写一行诊断汇总到日志（path / cached / missing 数）
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(3000);
            var total = paths.Count;
            var cached = paths.Count(p => Infrastructure.ThumbnailCache.Shared.PeekImage(p) is not null);
            var fileExists = paths.Count(p => System.IO.File.Exists(p));
            Serilog.Log.Information(
                "[ThumbDiag] project={Pid} totalSegments={Total} thumbPaths={Paths} fileExists={Exist} cachedNow={Cached} missing={Miss}",
                project.Id, _vm.FilteredSegments.Count, total, fileExists, cached, total - cached);
        });
    }

    // ============ 工具栏 ============

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _vm.Filter.SearchText = SearchBox.Text;
        ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        _vm.ApplyFilter();
        _vm.RebuildGroups();
        UpdateStats();
        UpdateEmptyState();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e) => SearchBox.Text = string.Empty;

    /// <summary>台词进入编辑态时自动聚焦 TextBox 并全选，用户直接改（对齐 mac textEditorFocused）。</summary>
    private void TranscriptEditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsVisible)
        {
            // 布局/可见切换后再聚焦，否则 Focus 可能落空。
            tb.Dispatcher.BeginInvoke(new Action(() =>
            {
                tb.Focus();
                tb.CaretIndex = tb.Text.Length;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnViewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _vm.IsGridView = ViewModeCombo.SelectedIndex == 0;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _vm.SortByQuality = SortCombo.SelectedIndex == 1;
        _vm.ApplyFilter();
        _vm.RebuildGroups();
    }

    private void OnResetFilter(object sender, RoutedEventArgs e)
    {
        _vm.ResetFilter();
        SearchBox.Text = string.Empty;
        BuildTypeChips();
        _vm.RebuildGroups();
        UpdateStats();
        UpdateEmptyState();
    }

    // ============ 多选 ============

    private void OnToggleSelectionMode(object sender, RoutedEventArgs e)
    {
        _vm.SetSelectionMode(!_vm.IsSelectionMode);
        _vm.SyncSelectionModeToCards();
        UpdateSelectionToolbar();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        _vm.SelectAllVisible();
        _vm.SyncCheckedToCards();
    }

    private void OnInvertSelection(object sender, RoutedEventArgs e)
    {
        _vm.InvertSelectionVisible();
        _vm.SyncCheckedToCards();
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        _vm.ClearSelection();
        _vm.SyncCheckedToCards();
    }

    private void OnBatchExport(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSegmentIds.Count == 0) return;
        // 展开成「原版 + 各已生成配音变体」任务（VM 内重载 dubs），对齐 mac 变体批量导出。
        var jobs = _vm.BuildVariantExportJobs();
        var dialog = new BatchExportDialog(_variantExport, _settings, jobs)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
    }

    private void OnBatchDelete(object sender, RoutedEventArgs e)
    {
        var count = _vm.SelectedSegmentIds.Count;
        if (count == 0) return;
        var confirm = MessageBox.Show(
            $"确定要删除选中的 {count} 个分镜吗？删除后可按 Ctrl+Z 撤销。",
            "确认批量删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        // P0-16：删除失败（DB 保存异常）时不谎报成功，给人话提示 + 重试入口。
        var deleted = _vm.DeleteSelectedSegments();
        if (deleted is null)
        {
            Components.ToastService.Show("删除失败，请重试", Components.ToastStyle.Error);
            return;
        }
        RefreshAfterSegmentChange();

        // P0-10：压入撤销栈，Ctrl+Z 可一键恢复被删分镜。
        if (deleted.Count > 0)
        {
            Infrastructure.UndoStack.UndoManager.Shared.Push(
                new Infrastructure.UndoStack.DelegateUndoAction(
                    $"删除 {deleted.Count} 个分镜",
                    () =>
                    {
                        var n = _vm.RestoreSegments(deleted);
                        RefreshAfterSegmentChange();
                        Components.ToastService.Show(
                            n > 0 ? $"已恢复 {n} 个分镜" : "恢复失败，请重试",
                            n > 0 ? Components.ToastStyle.Success : Components.ToastStyle.Error);
                    }));
        }
        Components.ToastService.Show($"已删除 {count} 个分镜", Components.ToastStyle.Warning,
            "撤销", () => Infrastructure.UndoStack.UndoManager.Shared.Undo());
    }

    /// <summary>分镜增删后统一刷新分组/类型 chip/统计/工具栏/空态（删除与撤销恢复共用）。</summary>
    private void RefreshAfterSegmentChange()
    {
        _vm.RebuildGroups();
        BuildTypeChips();
        UpdateStats();
        UpdateSelectionToolbar();
        UpdateEmptyState();
    }

    private void OnSelectionChanged()
    {
        Dispatcher.Invoke(() =>
        {
            UpdateSelectionToolbar();
            _vm.SyncCheckedToCards();
        });
    }

    /// <summary>
    /// VM 触发的结构性变更（右键单删 / Ctrl+Z 恢复）后刷新 View 侧 chrome。
    /// VM 已自行 RebuildGroups（卡片增减已生效），这里只补统计 / 类型 chip / 工具栏 / 空态，避免重复 RebuildGroups。
    /// </summary>
    private void OnSegmentsStructurallyChanged()
    {
        Dispatcher.Invoke(() =>
        {
            BuildTypeChips();
            UpdateStats();
            UpdateSelectionToolbar();
            UpdateEmptyState();
        });
    }

    private void UpdateSelectionToolbar()
    {
        if (_vm.IsSelectionMode)
        {
            SelectionToolbar.Visibility = Visibility.Visible;
            SelectionModeButton.Content = "✕ 退出多选";
        }
        else
        {
            SelectionToolbar.Visibility = Visibility.Collapsed;
            SelectionModeButton.Content = "☑ 多选";
        }
        var n = _vm.SelectedSegmentIds.Count;
        SelectionCountText.Text = $"已选 {n}";
        BatchExportButton.IsEnabled = n > 0;
        BatchDeleteButton.IsEnabled = n > 0;
        CombineSchemeButton.IsEnabled = n >= 2;
    }

    private async void OnCombineSchemeClick(object sender, RoutedEventArgs e)
    {
        // async void 必须包 try/catch（CLAUDE.md §UI 标准）
        try
        {
            var selected = _vm.SelectedSegmentsInOrder;
            if (selected.Count < 2)
            {
                return;
            }

            var sheet = new SegmentLibrary.ArrangeOrderSheet(selected)
            {
                Owner = Window.GetWindow(this),
            };
            if (sheet.ShowDialog() != true || sheet.Result is null)
            {
                return;
            }

            if (_currentProject is not Project project
                || Window.GetWindow(this) is not MainWindow mainWin)
            {
                return;
            }

            Components.ToastService.Show("正在生成自定义方案…", Components.ToastStyle.Info);

            var schemeVm = mainWin.SchemeViewModel;
            schemeVm.LoadSchemes(project);

            var scheme = await schemeVm.CreateCustomSchemeAsync(sheet.Result, project);
            if (scheme is not null)
            {
                Components.ToastService.Show(
                    $"已生成方案「{scheme.Name}」", Components.ToastStyle.Success);
                mainWin.NavigateToSchemesAndSelect(scheme);
                _vm.SetSelectionMode(false);
                _vm.SyncSelectionModeToCards();
                UpdateSelectionToolbar();
            }
            else
            {
                Components.ToastService.Show(
                    "生成失败，请检查 AI Key 或网络", Components.ToastStyle.Warning);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[SegmentLibraryViewV2.OnCombineSchemeClick] 异常: {Message}", ex.Message);
            // §红线：ex.Message 可能含原始异常文本 —— 翻成人话（AI 异常本就是中文，FFmpeg/其它走兜底）。
            Components.ToastService.Show($"组合失败: {MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex)}", Components.ToastStyle.Warning);
        }
    }

    // ============ 类型 chip ============

    private void BuildTypeChips()
    {
        var counts = _vm.CountByType();
        var chips = new List<UIElement>();
        foreach (var type in SemanticTypeExtensions.All)
        {
            var isSelected = _vm.Filter.SemanticTypes.Contains(type);
            var count = counts.GetValueOrDefault(type, 0);
            var isEmpty = count == 0;
            var color = (Color)ColorConverter.ConvertFromString(type.ToColorHex());
            var displayColor = isEmpty ? Color.FromRgb(0x99, 0x99, 0x99) : color;

            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(new TextBlock
            {
                Text = type.ToLabel(),
                FontSize = 11,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = new SolidColorBrush(isSelected ? Colors.White : displayColor),
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(
                    (byte)(isSelected ? 0x40 : 0x20), displayColor.R, displayColor.G, displayColor.B)),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = count.ToString(CultureInfo.InvariantCulture),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(isSelected ? Colors.White : displayColor),
                },
            });

            var btn = new Button
            {
                Tag = type,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(9, 4, 9, 4),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(
                    (byte)(isSelected ? 0x66 : 0x26), displayColor.R, displayColor.G, displayColor.B)),
                Background = new SolidColorBrush(Color.FromArgb(
                    (byte)(isSelected ? 0xC0 : 0x10), displayColor.R, displayColor.G, displayColor.B)),
                Content = stack,
                Opacity = isEmpty ? 0.35 : 1.0,
            };
            btn.Click += OnTypeChipClick;
            chips.Add(btn);
        }
        TypeChipList.ItemsSource = chips;
    }

    private void OnTypeChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SemanticType type })
        {
            if (!_vm.Filter.SemanticTypes.Add(type))
            {
                _vm.Filter.SemanticTypes.Remove(type);
            }
            _vm.ApplyFilter();
            _vm.RebuildGroups();
            BuildTypeChips();
            UpdateStats();
            UpdateEmptyState();
        }
    }

    private void UpdateStats()
    {
        var stats = _vm.Statistics();
        StatsText.Text =
            $"{_vm.FilteredSegments.Count} / {stats.Total} 个分镜 · 平均质量 " +
            stats.AverageQuality.ToString("F1", CultureInfo.InvariantCulture);
        ResetButton.Visibility =
            (_vm.Filter.SemanticTypes.Count > 0 || !string.IsNullOrEmpty(_vm.Filter.SearchText))
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEmptyState()
    {
        var isEmpty = _vm.FilteredSegments.Count == 0;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        GroupsScroller.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    // ============ 卡片事件 + 懒加载播放器 ============

    /// <summary>向上遍历视觉树，判断点击是否落在交互控件内（按钮/文本框/滚动条/遮挡框）。</summary>
    private static bool IsInsideInteractive(DependencyObject? child)
    {
        while (child is not null)
        {
            if (child is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.CheckBox
                or System.Windows.Controls.TextBox
                or System.Windows.Controls.Primitives.ScrollBar
                or SegmentLibrary.SubtitleMaskOverlay)
                return true;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    /// <summary>
    /// 卡片点击（PreviewMouseLeftButtonDown 隧道阶段）→ 选中弹变体池。
    /// 用 Preview 因为 hover 自动播放后 InlineVideoPlayer 会盖住缩略图并吞掉冒泡点击，
    /// 隧道阶段 CardRoot 先收到，保证「hover 播放 + 点击选中」不冲突（对齐 mac tap 选中）。
    /// 不 set Handled：让按钮/遮挡框/播放器内部控件继续收到点击。
    /// </summary>
    private void OnCardClicked(object sender, MouseButtonEventArgs e)
    {
        // 点在按钮/文本框/滚动条/遮挡框内 → 不选中，让那些控件自己处理。
        if (IsInsideInteractive(e.OriginalSource as DependencyObject))
            return;

        if (sender is FrameworkElement { Tag: SegmentCardViewModel card })
        {
            card.HandleCardClick();
        }
    }

    /// <summary>
    /// hover 中的 player 与 CardVM 时间字段的双向同步：调时间时实时刷新 player 的 segment 范围。
    /// 用 ConditionalWeakTable 把 handler 绑到 player 实例上，MouseLeave 时取消订阅。
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Components.InlineVideoPlayer, PropertyChangedEventHandler> _playerHandlers = new();

    /// <summary>hover 自动播放定时器（对齐 mac：进卡 0.35s 触发，离卡取消）。每卡一个，存于 VideoHost。</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SegmentCardViewModel, System.Windows.Threading.DispatcherTimer> _hoverTimers = new();

    private void OnCardMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SegmentCardViewModel card } cardRoot) return;
        card.IsHovering = true;
        if (!card.IsVideoFileAvailable) return;
        if (IsSelectionModeActive()) return; // 多选态不自动播

        // 起 0.35s 定时器，到时自动播放（对齐 mac hoverTimer）。
        CancelHoverTimer(card);
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        timer.Tick += (_, _) =>
        {
            CancelHoverTimer(card);
            if (card.IsHovering) StartHoverPlay(card, cardRoot);
        };
        _hoverTimers.AddOrUpdate(card, timer);
        timer.Start();
    }

    /// <summary>
    /// 点击缩略图 / ▶ → 显式（重新）播放该分镜。补 hover 自动播放的盲区：
    /// 已在卡内、播完 / 调帧微调后 MouseEnter 不会再触发，用户点播放「没反应」（用户反馈：
    /// 调帧后必须移出空白处再移回才播）。StartHoverPlay 内已 guard「正在播则忽略」，重复点不抖。
    /// </summary>
    private void OnPlayOverlayClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (FindAncestorByName(fe, "CardRoot") is not FrameworkElement { Tag: SegmentCardViewModel card } cardRoot) return;
        if (!card.IsVideoFileAvailable) return;
        if (IsSelectionModeActive()) return; // 多选态点击用于勾选，不播放
        CancelHoverTimer(card);
        StartHoverPlay(card, cardRoot);
    }

    /// <summary>#12：点「切换画面」胶囊 → 在原画面 / AI 替换画面间切换（不触发播放）。</summary>
    private void OnTogglePictureClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // 不冒泡到卡片播放 / 选中
        if ((sender as FrameworkElement)?.DataContext is SegmentCardViewModel card
            && card.ToggleReplacedPictureCommand.CanExecute(null))
        {
            card.ToggleReplacedPictureCommand.Execute(null);
        }
    }

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SegmentCardViewModel card }) return;
        card.IsHovering = false;
        CancelHoverTimer(card);
        // 离卡停止播放、还原缩略图。
        StopHoverPlay(card);
    }

    private void CancelHoverTimer(SegmentCardViewModel card)
    {
        if (_hoverTimers.TryGetValue(card, out var t)) { t.Stop(); _hoverTimers.Remove(card); }
    }

    private bool IsSelectionModeActive() =>
        DataContext is SegmentLibraryViewModel { IsSelectionMode: true };

    /// <summary>停止某卡的 hover 播放并还原缩略图（离卡 / 被抢占时调用）。</summary>
    private void StopHoverPlay(SegmentCardViewModel card)
    {
        var cardRoot = FindCardRootFor(card);
        if (cardRoot is null) return;
        var videoHost = FindChild<ContentControl>(cardRoot, "VideoHost");
        if (videoHost?.Content is Components.InlineVideoPlayer player)
        {
            player.StopPlayback();
        }
    }

    /// <summary>从可见卡片树里找到承载指定 card 的 CardRoot Border。</summary>
    private FrameworkElement? FindCardRootFor(SegmentCardViewModel card)
    {
        foreach (var fe in EnumerateVisualChildren(this))
        {
            if (fe is FrameworkElement { Name: "CardRoot", Tag: SegmentCardViewModel c } cr && ReferenceEquals(c, card))
                return cr;
        }
        return null;
    }

    private static IEnumerable<FrameworkElement> EnumerateVisualChildren(DependencyObject root)
    {
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe) yield return fe;
            foreach (var deep in EnumerateVisualChildren(child)) yield return deep;
        }
    }

    /// <summary>境界窗口预览的播放时长（秒）：调 IN 从起点播这么久、调 OUT 播最后这么久。对齐 Mac「点加减播几秒看结果」。</summary>
    private const double BoundaryPreviewSeconds = 2.0;

    /// <summary>调 IN/OUT 后的境界预览播放 debounce（每卡一个）：连点微调只在停手 ~300ms 后播一次窗口，避免每帧重启 ffmpeg。</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SegmentCardViewModel, System.Windows.Threading.DispatcherTimer> _boundaryTimers = new();

    /// <summary>
    /// 在该卡 VideoHost 里懒创建（或复用）内联播放器，挂 Idle 还原逻辑（播完 / 停止 / 被抢占 → 拆播放器 + 还原 ▶/角标）。
    /// 不再挂「时间字段实时同步 SetSegment(整段)」监听 —— 调帧改由 <see cref="PlayBoundaryPreview"/> 播境界窗口取代
    /// （旧监听会与窗口预览抢 SetSegment 导致范围打架，见 §不要破坏已有功能）。
    /// </summary>
    private Components.InlineVideoPlayer EnsureCardPlayer(
        ContentControl videoHost, Grid? playBtn, Border? badge, SegmentCardViewModel card)
    {
        if (videoHost.Content is Components.InlineVideoPlayer existing) return existing;
        var player = new Components.InlineVideoPlayer
        {
            AutoPlayOnHover = false,
            VideoStretch = System.Windows.Media.Stretch.UniformToFill,
        };
        player.Idle += (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // 仅当仍是本 player 才还原：避免旧 player 播完的 Idle 把刚换上的新预览的 ▶ 闪出来。
                if (videoHost.Content == player)
                {
                    TeardownHoverPlayer(videoHost, player, card);
                    if (playBtn is not null) playBtn.Visibility = Visibility.Visible;
                    if (badge is not null) badge.Visibility = Visibility.Visible;
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        };
        videoHost.Content = player;
        return player;
    }

    /// <summary>用宿主已布局尺寸预置解码目标（竖屏框），免去昂贵同步 UpdateLayout（数十卡 ~150-200ms）；刚塞进去的 player 自身 ActualWidth=0。</summary>
    private static void PrimePlayer(Components.InlineVideoPlayer player, ContentControl videoHost)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(videoHost);
        player.PrimeDecodeSize(
            (int)Math.Round(videoHost.ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(videoHost.ActualHeight * dpi.DpiScaleY));
    }

    /// <summary>
    /// 点击 / hover 到时 → 在该卡 VideoHost 里懒创建内联播放器，播放<b>整段</b>（起点→终点）。
    /// 播完 / 停止 / 被其他卡抢占由 player.Idle 还原缩略图。
    /// </summary>
    private void StartHoverPlay(SegmentCardViewModel card, FrameworkElement cardRoot)
    {
        if (!card.IsVideoFileAvailable) return;

        var thumbGrid = FindChild<Grid>(cardRoot, "ThumbGrid");
        if (thumbGrid is null) return;
        var videoHost = FindChild<ContentControl>(thumbGrid, "VideoHost");
        if (videoHost is null) return;
        var badge = FindChild<Border>(thumbGrid, "CardDurationBadge");
        var playBtn = FindChild<Grid>(thumbGrid, "PlayOverlay");

        // 已经在播这张卡 → 忽略，避免重复 Open 抖动。
        if (videoHost.Content is Components.InlineVideoPlayer { IsPlaying: true })
        {
            return;
        }

        Serilog.Log.Information(
            "[SegPlayDiag] 点击/hover 播放 seq={Seq} startFrame={SF}",
            card.SequenceNumber, card.Segment.StartFrame);

        var player = EnsureCardPlayer(videoHost, playBtn, badge, card);
        if (playBtn is not null) playBtn.Visibility = Visibility.Collapsed;
        if (badge is not null) badge.Visibility = Visibility.Collapsed;
        PrimePlayer(player, videoHost);
        // #12：走 EffectivePicture —— 有 AI 替换画面则播替换视频（0..替换帧数），否则播原片段。未替换时返回原值。
        var ep = card.Segment.EffectivePicture;
        player.PlaySegment(ep.VideoPath, card.ThumbnailPath, ep.StartFrame, ep.EndFrame, ep.Fps);
    }

    /// <summary>
    /// VM 调 IN/OUT 帧后广播的境界预览请求 → debounce ~300ms 播放境界窗口（对齐 Mac：调开头从起点播几秒、
    /// 调结尾从末尾前几秒播到最后，便于查看调整结果）。连点微调合并成一次播放，避免每帧重启 ffmpeg
    /// （静止帧 <c>ScrubImage</c> 已给每点即时反馈，这里补动态）。
    /// </summary>
    private void OnBoundaryPreviewRequested(SegmentCardViewModel card, bool isStart)
    {
        if (!card.IsVideoFileAvailable) return;
        if (IsSelectionModeActive()) return; // 多选态不播（点击用于勾选）

        if (_boundaryTimers.TryGetValue(card, out var old)) { old.Stop(); _boundaryTimers.Remove(card); }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _boundaryTimers.Remove(card);
            PlayBoundaryPreview(card, isStart);
        };
        _boundaryTimers.AddOrUpdate(card, timer);
        timer.Start();
    }

    /// <summary>
    /// 播放境界窗口：isStart → [起点, 起点+N秒)；否则 → [末尾-N秒, 末尾)（EndFrame 不含，播到 EndFrame-1 冻结）。
    /// 复用 / 新建卡片播放器均走 <see cref="Components.InlineVideoPlayer.PlaySegment"/>（恒定一次 Open）。
    /// </summary>
    private void PlayBoundaryPreview(SegmentCardViewModel card, bool isStart)
    {
        if (!card.IsVideoFileAvailable) return;
        if (IsSelectionModeActive()) return;

        var cardRoot = FindCardRootFor(card);
        if (cardRoot is null) return;
        var thumbGrid = FindChild<Grid>(cardRoot, "ThumbGrid");
        if (thumbGrid is null) return;
        var videoHost = FindChild<ContentControl>(thumbGrid, "VideoHost");
        if (videoHost is null) return;
        var badge = FindChild<Border>(thumbGrid, "CardDurationBadge");
        var playBtn = FindChild<Grid>(thumbGrid, "PlayOverlay");

        var fps = card.Segment.EffectiveFps;
        if (fps <= 0) return; // 无 fps 无法帧窗预览（静止帧已给反馈）
        var segStart = Math.Max(0, card.Segment.StartFrame);
        var segEnd = card.Segment.EndFrame;
        if (segEnd <= segStart) return;

        var previewFrames = Math.Max(1, (int)Math.Round(BoundaryPreviewSeconds * fps));
        int winStart, winEnd;
        if (isStart) { winStart = segStart; winEnd = Math.Min(segEnd, segStart + previewFrames); }
        else { winEnd = segEnd; winStart = Math.Max(segStart, segEnd - previewFrames); }
        if (winEnd <= winStart) winEnd = winStart + 1;

        Serilog.Log.Information(
            "[BoundaryPreviewDiag] seq={Seq} isStart={IsStart} win=[{A},{B}) fps={Fps:F3}",
            card.SequenceNumber, isStart, winStart, winEnd, fps);

        var player = EnsureCardPlayer(videoHost, playBtn, badge, card);
        if (playBtn is not null) playBtn.Visibility = Visibility.Collapsed;
        if (badge is not null) badge.Visibility = Visibility.Collapsed;
        PrimePlayer(player, videoHost);
        player.PlaySegment(card.VideoLocalPath!, card.ThumbnailPath, winStart, winEnd, fps);
    }

    /// <summary>从子元素沿可视树向上查找指定 x:Name 的祖先。</summary>
    private static FrameworkElement? FindAncestorByName(DependencyObject? d, string name)
    {
        while (d is not null)
        {
            if (d is FrameworkElement fe && fe.Name == name) return fe;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>拆掉点击创建的内联播放器：解绑时间同步监听 + 从 VideoHost 移除（还原静态缩略图）。</summary>
    private void TeardownHoverPlayer(ContentControl videoHost, Components.InlineVideoPlayer player, SegmentCardViewModel card)
    {
        if (_playerHandlers.TryGetValue(player, out var handler))
        {
            card.PropertyChanged -= handler;
            _playerHandlers.Remove(player);
        }
        if (videoHost.Content == player)
        {
            videoHost.Content = null;
        }
    }

    // ============ 时间编辑框 commit ============

    private void OnStartLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: SegmentCardViewModel card } tb)
        {
            card.CommitStartCommand.Execute(tb.Text);
            tb.Text = card.StartTime.ToString("F1", CultureInfo.InvariantCulture);
        }
    }

    private void OnEndLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: SegmentCardViewModel card } tb)
        {
            card.CommitEndCommand.Execute(tb.Text);
            tb.Text = card.EndTime.ToString("F1", CultureInfo.InvariantCulture);
        }
    }

    private void OnStartKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnEndKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    // ============ QuickEdit ============

    private void OnQuickEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SegmentCardViewModel card } btn) return;

        var menu = new ContextMenu();
        foreach (var type in SemanticTypeExtensions.All)
        {
            var item = new MenuItem
            {
                Header = type.ToLabel(),
                IsCheckable = true,
                IsChecked = card.SemanticTypes.Contains(type),
            };
            item.Click += (_, _) => card.ToggleSemanticCommand.Execute(type);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        foreach (var pos in PositionTypeExtensions.All)
        {
            var item = new MenuItem
            {
                Header = pos.ToLabel(),
                IsCheckable = true,
                IsChecked = card.PositionType == pos,
            };
            item.Click += (_, _) => card.UpdatePositionCommand.Execute(pos);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = btn;
        menu.IsOpen = true;
    }

    // ============ 快捷键 ============

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;

        if (e.Key == Key.Escape && _vm.IsSelectionMode)
        {
            _vm.SetSelectionMode(false);
            _vm.SyncSelectionModeToCards();
            UpdateSelectionToolbar();
            e.Handled = true;
            return;
        }

        if (!_vm.IsSelectionMode) return;
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        switch (e.Key)
        {
            case Key.A:
                _vm.SelectAllVisible();
                _vm.SyncCheckedToCards();
                e.Handled = true;
                break;
            case Key.D:
                _vm.InvertSelectionVisible();
                _vm.SyncCheckedToCards();
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                _vm.ClearSelection();
                _vm.SyncCheckedToCards();
                e.Handled = true;
                break;
        }
    }

    // ============ Helper ============

    /// <summary>在 Visual Tree 里按 name 找子元素。用于 DataTemplate 实例化的元素查找。</summary>
    private static T? FindChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.Name == name) return t;
            var found = FindChild<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }
}
