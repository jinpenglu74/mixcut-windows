using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MixCut.Models;
using MixCut.Services.ShotEdit;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>
/// 分镜头 AI 画面替换工作区（全屏模态）。对应 macOS ShotEditSheet。
/// XAML 只提供命名容器；轨道/编辑行/版本区由本代码后置按 VM 状态动态构建（对齐 SchemesView 模式）。
/// </summary>
public partial class ShotEditWindow : Window
{
    private readonly ShotEditViewModel _vm;
    private readonly Segment _segment;
    private readonly Utilities.AppSettings? _settings;
    private TextBox? _promptBox;
    private FrameworkElement? _selectedShotCard;   // 当前选中镜头卡（切换后自动滚动到可见）

    // 预设提示词模板（PRD §5.3）：分组 → 各条。
    private static readonly (string Group, string[] Items)[] Presets =
    {
        ("换背景", new[]
        {
            "把背景换成明亮整洁的现代白色厨房，保持前景的手和产品完全不变",
            "把背景换成夕阳下的海边沙滩",
            "把画面背景里那幅画换成梵高的《星月夜》",
        }),
        ("换主体外观 / 人物", new[]
        {
            "把画面里的长发女孩换成利落的短发",
            "把手里拿的可乐换成一瓶雪碧，手的姿势和背景保持不变",
        }),
        ("换颜色 / 材质", new[]
        {
            "把女孩的睡衣从粉色换成蓝色",
            "把红色马克杯换成透明玻璃杯",
        }),
        ("整体风格", new[] { "把整个画面调成清晨柔光、暖色调" }),
    };

    public ShotEditWindow(ShotEditViewModel vm, Segment segment, Utilities.AppSettings? settings = null)
    {
        InitializeComponent();
        _vm = vm;
        _segment = segment;
        _settings = settings;
        if (settings is not null) { Width = settings.ShotEditWidth; Height = settings.ShotEditHeight; }
        _vm.Changed += OnVmChanged;
        _vm.VariantProgress += OnVariantProgress;
        Loaded += async (_, _) =>
        {
            try
            {
                RebuildAll();
                await _vm.LoadShotsAsync(segment);
            }
            catch (Exception ex)
            {
                ShowError("没能把这个分镜切成分镜头。可能是源视频已被移动或删除，也可能这段素材太短、没有明显的画面切换",
                    ex, () => _vm.LoadShotsAsync(_segment));
            }
        };
        Closed += (_, _) =>
        {
            _vm.Changed -= OnVmChanged;
            _vm.VariantProgress -= OnVariantProgress;
            // 记忆工作区尺寸（用户拉大看更多镜头，下次还在）。
            if (_settings is not null && WindowState == WindowState.Normal)
            {
                try
                {
                    if (ActualWidth >= 780) _settings.ShotEditWidth = ActualWidth;
                    if (ActualHeight >= 600) _settings.ShotEditHeight = ActualHeight;
                }
                catch { /* 忽略 */ }
            }
        };
        // 键盘：ESC 关闭（合成中不关）；←/→ 切换选中镜头（power-user）。焦点在提示词输入框时不拦左右键。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && !_vm.IsComposing) { Close(); e.Handled = true; return; }
            if (System.Windows.Input.Keyboard.FocusedElement is TextBox) return;   // 输入台词时左右键用于移动光标
            if (_vm.Shots.Count == 0) return;
            if (e.Key is System.Windows.Input.Key.Left or System.Windows.Input.Key.Right)
            {
                var idx = _vm.Shots.FindIndex(s => s.OrderIndex == _vm.SelectedOrderIndex);
                if (idx < 0) idx = 0;
                idx = e.Key == System.Windows.Input.Key.Left ? Math.Max(0, idx - 1) : Math.Min(_vm.Shots.Count - 1, idx + 1);
                _vm.SelectedOrderIndex = _vm.Shots[idx].OrderIndex;
                RebuildAll();
                e.Handled = true;
            }
        };
    }

    private void OnVmChanged() => Dispatcher.Invoke(RebuildAll);

    private void OnVariantProgress(Guid variantId, string status) => Dispatcher.Invoke(RebuildAll);

    // ---- 顶层重建 ----

    private void RebuildAll()
    {
        CaptionText.Text = _vm.Shots.Count > 0
            ? $"{_vm.Shots.Count} 个镜头 · 共 {_vm.SegmentEnd - _vm.SegmentStart:F1}s · {_vm.SegmentCaption}"
            : _vm.SegmentCaption;
        ComposeButton.IsEnabled = _vm.CanCompose && !_vm.IsComposing;
        ComposeButton.Content = _vm.IsComposing ? _vm.ComposeStatus : "合成新分镜";
        ComposeButton.ToolTip = _vm.IsComposing ? "正在合成，请稍候…"
            : _vm.CanCompose ? "把各分镜头选定的版本拼成新画面，就地替换本分镜"
            : "先给每个分镜头位置各选一个版本（原版或已完成变体）才能合成";
        CloseBtn.IsEnabled = !_vm.IsComposing;   // 合成中禁用关闭，防中断落库

        // 走统一出口：瞬时错误（用户刚触发的）优先于 VM 的持久错误，且不会被这次刷新抹掉。
        // 原来这里是「VM 没错就折叠横幅」，而 RebuildAll 每收到一次变体进度就跑一遍 ——
        // 用户删除变体失败弹出的红字，会被下一个变体的进度回调瞬间清掉。
        ApplyErrorBanner();

        BuildTrack();
        BuildEditRow();
        BuildVersionPanel();
    }

    /// <summary>
    /// 用户手动触发的瞬时错误。与 VM 的持久 ErrorMessage 分开存 ——
    /// 否则 RebuildAll（每次变体进度回调都会跑）会因为 _vm.ErrorMessage 为 null 把横幅直接折叠掉，
    /// 用户只看到红字一闪而过，根本来不及读（删除变体失败时最容易撞上）。
    /// </summary>
    private string? _transientError;

    /// <summary>瞬时错误对应的重试动作；为 null 时横幅不显示「重试」。</summary>
    private Func<Task>? _transientRetry;

    private void ShowError(string msg)
    {
        _transientError = msg;
        _transientRetry = null;
        ApplyErrorBanner();
    }

    /// <summary>
    /// 统一错误出口：翻成人话 + 原始异常进日志 + 可选重试入口。
    ///
    /// 本窗口原有 10 处 `ShowError("xxx失败：" + ex.Message)`，底层全是 ffmpeg 与 DashScope，
    /// 于是用户会看到「合并失败：视频处理失败 (exit -1073741515): ...」这种东西 ——
    /// 而这里还是**付费**路径，最不该出现这种界面。
    /// </summary>
    private void ShowError(string what, Exception ex, Func<Task>? retry = null)
    {
        Serilog.Log.Error(ex, "[ShotEditDiag] {What}", what);
        _transientError = $"{what}：{MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex)}";
        _transientRetry = retry;
        ApplyErrorBanner();
    }

    private void ApplyErrorBanner()
    {
        var msg = _transientError ?? _vm.ErrorMessage;
        if (string.IsNullOrEmpty(msg))
        {
            ErrorBanner.Visibility = Visibility.Collapsed;
            return;
        }
        ErrorText.Text = msg;
        ErrorRetryButton.Visibility = _transientRetry is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void OnErrorCloseClick(object sender, RoutedEventArgs e)
    {
        _transientError = null;
        _transientRetry = null;
        ErrorBanner.Visibility = Visibility.Collapsed;
    }

    private async void OnErrorRetryClick(object sender, RoutedEventArgs e)
    {
        var retry = _transientRetry;
        if (retry is null) return;
        _transientError = null;
        _transientRetry = null;
        ErrorBanner.Visibility = Visibility.Collapsed;
        try
        {
            await retry();
        }
        catch (Exception ex)
        {
            ShowError("重试仍未成功", ex);
        }
    }

    // ---- [B] 轨道 ----

    private void BuildTrack()
    {
        TrackPanel.Children.Clear();

        if (_vm.IsSlicing)
        {
            var loading = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(8, 90, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            loading.Children.Add(new ProgressBar
            {
                IsIndeterminate = true, Width = 120, Height = 4, VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("AccentBlueBrush"),
            });
            loading.Children.Add(new TextBlock
            {
                Text = "正在准备分镜头…（切分 + 生成预览）", FontSize = 13, Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextSecondaryBrush"),
            });
            TrackPanel.Children.Add(loading);
            return;
        }
        if (_vm.Shots.Count == 0)
        {
            var empty = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 80, 4, 0) };
            empty.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(_vm.ErrorMessage)
                    ? "未能切出分镜头（该分镜可能过短或无画面切换）" : _vm.ErrorMessage,
                FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextSecondaryBrush"),
            });
            var retry = new Button
            {
                Content = "重新切分", FontSize = 12, Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4),
                Cursor = System.Windows.Input.Cursors.Hand, Background = Brush("BgSubtleBrush"),
                Foreground = Brush("TextSecondaryBrush"), BorderThickness = new Thickness(0),
            };
            retry.Click += async (_, _) =>
            {
                try { await _vm.LoadShotsAsync(_segment); }
                catch (Exception ex) { ShowError("仍然没能切分。请确认原视频还在原位置", ex); }
            };
            empty.Children.Add(retry);
            TrackPanel.Children.Add(empty);
            return;
        }

        _selectedShotCard = null;
        for (var i = 0; i < _vm.Shots.Count; i++)
        {
            TrackPanel.Children.Add(BuildShotCard(_vm.Shots[i]));
            if (i < _vm.Shots.Count - 1)
            {
                TrackPanel.Children.Add(BuildBoundaryControl(i));
            }
        }
        // 选中的镜头卡滚动到可见（方向键/点击切换后，屏外的选中卡自动露出）。
        if (_selectedShotCard is not null)
        {
            Dispatcher.BeginInvoke(new Action(() => _selectedShotCard?.BringIntoView()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private UIElement BuildShotCard(PhysicalShot shot)
    {
        var selected = _vm.SelectedOrderIndex == shot.OrderIndex;
        var durSec = _vm.DurationOf(shot);
        var editable = _vm.IsEditable(shot);
        var chosenVariant = _vm.Selections.TryGetValue(shot.OrderIndex, out var vid) ? vid : null;

        var stack = new StackPanel { Width = 96, Margin = new Thickness(0) };
        if (selected) _selectedShotCard = stack;

        // 预览（9:16）+ 角标 + 选中描边
        var previewBorder = new Border
        {
            Width = 96, Height = 170, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            BorderBrush = selected ? Brush("AccentBlueBrush") : Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 2.5 : 0),
            ClipToBounds = true,
        };
        var host = new Grid();
        var img = LoadImage(shot.ThumbnailPath, 96);
        Border? playHint = null;
        if (img != null)
        {
            host.Children.Add(new Image { Source = img, Stretch = Stretch.UniformToFill });
            // 居中 ▶ 播放提示（点它播放该镜头源片段）
            playHint = MakePlayHint(34);
            playHint.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;   // 不冒泡到卡片（避免同时触发选中重建）
                PlayShotInHost(host, playHint, shot.ThumbnailPath, shot.StartFrame, shot.EndFrame);
            };
            host.Children.Add(playHint);
        }
        else
        {
            // 缩略图未就绪 → 显示 loading，而不是纯黑（用户反馈）
            host.Children.Add(MakeLoadingOverlay("加载预览…"));
        }
        // 「镜头 N」角标
        host.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 2, 5, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            Child = new TextBlock { Text = $"镜头 {shot.OrderIndex}", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold },
        });
        previewBorder.Child = host;
        previewBorder.MouseLeftButtonUp += (_, _) => { _vm.SelectedOrderIndex = shot.OrderIndex; RebuildAll(); };
        previewBorder.Cursor = System.Windows.Input.Cursors.Hand;
        // hover 微缩放反馈（与分镜库卡片一致；纯 RenderTransform，不碰画刷）。
        previewBorder.RenderTransformOrigin = new Point(0.5, 0.5);
        var cardScale = new ScaleTransform(1, 1);
        previewBorder.RenderTransform = cardScale;
        previewBorder.MouseEnter += (_, _) => { cardScale.ScaleX = cardScale.ScaleY = 1.04; };
        previewBorder.MouseLeave += (_, _) => { cardScale.ScaleX = cardScale.ScaleY = 1.0; };
        stack.Children.Add(previewBorder);

        // 时长
        stack.Children.Add(new TextBlock
        {
            Text = $"{durSec:F1}s", FontSize = 10, Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 4, 0, 0),
        });

        // 资格 / 选择状态标签
        string label; Brush color;
        if (!editable) { label = "超限·仅原版"; color = new SolidColorBrush(Color.FromRgb(0xC0, 0x6F, 0x00)); }
        else if (chosenVariant is not null) { label = "已选变体"; color = Brush("AccentBlueBrush"); }
        else { label = "原版"; color = Brush("TextTertiaryBrush"); }
        stack.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = color, Margin = new Thickness(0, 1, 0, 0) });

        return stack;
    }

    private UIElement BuildBoundaryControl(int leftShotIndex)
    {
        var panel = new StackPanel
        {
            Width = 28, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0),
        };

        // 可拖动分界手柄（对齐 PRD §4.2：左右拖动改边界 ~2px≈1帧，松手一次性落库；精调仍可用下方 ±帧）。
        var handle = new Border
        {
            Width = 6, Height = 170, CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(0x8C, 0x1D, 0x6B, 0xE5)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.SizeWE,
            ToolTip = "左右拖动改这两个镜头的分界（约 2px≈1 帧），松手生效",
        };
        var xform = new TranslateTransform();
        handle.RenderTransform = xform;
        // 拖动中的实时帧数气泡（Popup 浮层，不影响轨道布局）。
        var deltaText = new TextBlock { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold };
        var deltaPopup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = handle,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            AllowsTransparency = true, StaysOpen = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1D, 0x6B, 0xE5)),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Child = deltaText,
            },
        };
        var dragging = false;
        var startX = 0.0;
        handle.MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
            startX = e.GetPosition(this).X;
            handle.CaptureMouse();
            handle.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1D, 0x6B, 0xE5));  // 拖动中高亮
            deltaText.Text = "0 帧";
            deltaPopup.IsOpen = true;
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var dx = e.GetPosition(this).X - startX;
            xform.X = dx;                                   // 手柄跟随光标
            var df = (int)Math.Round(dx / 2.0);            // 2px≈1帧
            deltaText.Text = df > 0 ? $"+{df} 帧" : $"{df} 帧";
        };
        handle.MouseLeftButtonUp += async (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            handle.ReleaseMouseCapture();
            deltaPopup.IsOpen = false;
            handle.Background = new SolidColorBrush(Color.FromArgb(0x8C, 0x1D, 0x6B, 0xE5));
            var deltaFrames = (int)Math.Round((e.GetPosition(this).X - startX) / 2.0);   // 2px≈1帧
            xform.X = 0;
            if (deltaFrames != 0)
            {
                try { await _vm.NudgeBoundaryAsync(leftShotIndex, deltaFrames); }
                catch (Exception ex) { ShowError("边界没调成功，手柄已复位，改动未保存", ex); }
            }
        };
        panel.Children.Add(handle);

        // 合并按钮
        var mergeBtn = new Button
        {
            Content = "⇥⇤", FontSize = 11, Margin = new Thickness(0, 6, 0, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Brush("TextSecondaryBrush"), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "合并这两个镜头", Padding = new Thickness(2),
        };
        mergeBtn.Click += async (_, _) =>
        {
            try { await _vm.MergeShotsAsync(leftShotIndex); }
            catch (Exception ex) { ShowError("这两个镜头没能合并，画面未改动。可以重试一次，或改用「拆分」重新划分边界", ex); }
        };
        panel.Children.Add(mergeBtn);
        return panel;
    }

    // ---- 选中镜头编辑行 ----

    private void BuildEditRow()
    {
        EditRowPanel.Children.Clear();
        var idx = _vm.Shots.FindIndex(s => s.OrderIndex == _vm.SelectedOrderIndex);
        if (idx < 0) return;
        var shot = _vm.Shots[idx];

        EditRowPanel.Children.Add(new TextBlock
        {
            Text = $"镜头 {shot.OrderIndex} 边界", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextTertiaryBrush"), Margin = new Thickness(0, 0, 10, 0),
        });

        // 左边界（与上一镜头之间）= boundaryIndex idx-1
        if (idx > 0)
        {
            EditRowPanel.Children.Add(BuildStepper("左", idx - 1));
        }
        // 右边界（与下一镜头之间）= boundaryIndex idx
        if (idx < _vm.Shots.Count - 1)
        {
            EditRowPanel.Children.Add(BuildStepper("右", idx));
        }

        // 拆分
        var splitBtn = new Button
        {
            Content = "✂ 拆分", FontSize = 11, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3),
            Cursor = System.Windows.Input.Cursors.Hand, Background = Brush("BgSubtleBrush"),
            Foreground = Brush("TextSecondaryBrush"), BorderThickness = new Thickness(0),
            ToolTip = "把当前镜头从正中间一分为二",
        };
        splitBtn.Click += async (_, _) =>
        {
            try { await _vm.SplitShotAsync(shot.OrderIndex); }
            catch (Exception ex) { ShowError("没能拆分这个镜头。镜头太短时无法再拆（每段至少约 0.3 秒），可以先调整边界让它变长", ex); }
        };
        EditRowPanel.Children.Add(splitBtn);
    }

    private UIElement BuildStepper(string label, int boundaryIndex)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brush("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        panel.Children.Add(MakeNudgeBtn("−", boundaryIndex, -1));
        panel.Children.Add(MakeNudgeBtn("＋", boundaryIndex, +1));
        return panel;
    }

    private Button MakeNudgeBtn(string text, int boundaryIndex, int delta)
    {
        var b = new Button
        {
            Content = text, FontSize = 12, Width = 26, Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(0, 2, 0, 2),
            Cursor = System.Windows.Input.Cursors.Hand, Background = Brush("BgSubtleBrush"),
            Foreground = Brush("TextPrimaryBrush"), BorderThickness = new Thickness(0),
            ToolTip = "移动 1 帧",
        };
        b.Click += async (_, _) =>
        {
            try { await _vm.NudgeBoundaryAsync(boundaryIndex, delta); }
            catch (Exception ex) { ShowError("边界没调成功，改动未保存。若反复失败，关掉本窗口重新打开可恢复到上次保存的状态", ex); }
        };
        return b;
    }

    // ---- [C] 版本区 ----

    private void BuildVersionPanel()
    {
        VersionPanel.Children.Clear();
        _promptBox = null;
        var idx = _vm.Shots.FindIndex(s => s.OrderIndex == _vm.SelectedOrderIndex);
        if (idx < 0)
        {
            VersionPanel.Children.Add(new TextBlock
            {
                Text = "选择上方一个分镜头来替换", FontSize = 13, Foreground = Brush("TextTertiaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0),
            });
            return;
        }
        var shot = _vm.Shots[idx];
        var editable = _vm.IsEditable(shot);

        VersionPanel.Children.Add(new TextBlock
        {
            Text = $"镜头 {shot.OrderIndex} · 选一个版本用于合成", FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"), Margin = new Thickness(0, 0, 0, 10),
        });

        // 版本卡横向滚动
        var cardsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        cardsPanel.Children.Add(BuildOriginalCard(shot));
        foreach (var v in shot.Variants.OrderBy(x => x.CreatedAt))
        {
            cardsPanel.Children.Add(BuildVariantCard(shot, v));
        }
        VersionPanel.Children.Add(new ScrollViewer
        {
            Content = cardsPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 12),
        });

        // 提示词区（仅可编辑镜头）
        if (editable) VersionPanel.Children.Add(BuildPromptArea(shot));
        else VersionPanel.Children.Add(new TextBlock
        {
            Text = _vm.IneligibleReason(shot) ?? "该镜头不可替换", FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x6F, 0x00)), TextWrapping = TextWrapping.Wrap,
        });
    }

    private UIElement BuildOriginalCard(PhysicalShot shot)
    {
        var selected = _vm.Selections.TryGetValue(shot.OrderIndex, out var vid) && vid is null;
        var stack = new StackPanel { Width = 90, Margin = new Thickness(0, 0, 10, 0) };
        var border = VersionThumb(shot.ThumbnailPath, selected,
            (host, hint) => PlayShotInHost(host, hint, shot.ThumbnailPath, shot.StartFrame, shot.EndFrame));
        border.MouseLeftButtonUp += async (_, _) =>
        {
            // 不能吞：用户点了卡片，选中框没变又没有任何提示，只会反复点。
            try { await _vm.SelectAsync(shot.OrderIndex, null); }
            catch (Exception ex) { ShowError("没能选中「原版」，请再点一次", ex); }
        };
        stack.Children.Add(border);
        stack.Children.Add(new TextBlock { Text = "原版", FontSize = 10, Foreground = Brush("TextSecondaryBrush"), Margin = new Thickness(0, 4, 0, 0) });
        stack.Children.Add(new TextBlock { Text = $"{_vm.DurationOf(shot):F1}s", FontSize = 10, Foreground = Brush("TextTertiaryBrush") });
        return stack;
    }

    private UIElement BuildVariantCard(PhysicalShot shot, ShotVariant v)
    {
        var selected = _vm.Selections.TryGetValue(shot.OrderIndex, out var vid) && vid == v.Id;
        var busy = _vm.BusyVariantIds.Contains(v.Id) || v.Status == ShotVariantStatus.Generating;
        var stack = new StackPanel { Width = 90, Margin = new Thickness(0, 0, 10, 0) };

        Border border;
        if (busy)
        {
            // 生成中：黑底 + 无限进度条 + 文案（对齐 PRD §5.2 生成中转圈）。
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 54, Height = 4, Foreground = Brushes.White });
            sp.Children.Add(new TextBlock
            {
                Text = "AI 生成中…", Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)),
                FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            });
            sp.Children.Add(new TextBlock
            {
                // 长任务给预期，避免用户以为卡死（§商用丝滑标准 §2）。
                Text = "约 2~4 分钟", Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
            });
            border = new Border
            {
                Width = 90, Height = 160, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0xD9, 0, 0, 0)), Child = sp,
            };
        }
        else if (v.Status == ShotVariantStatus.TimedOut)
        {
            // 超时：琥珀感叹号（云端可能仍在跑，可重试拉取、不重复扣费）。区别于「失败」的红色。
            border = StatusThumb("⏱ 已超时", Color.FromRgb(0x35, 0x2C, 0x08), new SolidColorBrush(Color.FromRgb(0xC0, 0x6F, 0x00)));
            border.ToolTip = string.IsNullOrEmpty(v.FriendlyError)
                ? "本地等待超时，任务可能仍在云端生成，点「重试」重新获取，不会重复扣费。" : v.FriendlyError;
        }
        else if (v.Status == ShotVariantStatus.Failed)
        {
            // 失败：红色感叹号。
            border = StatusThumb("⚠ 失败", Color.FromRgb(0x35, 0x18, 0x18), new SolidColorBrush(Color.FromRgb(0xD3, 0x3A, 0x3A)));
            border.ToolTip = string.IsNullOrEmpty(v.FriendlyError) ? "生成失败" : v.FriendlyError;
        }
        else
        {
            border = VersionThumb(v.ThumbnailPath, selected,
                (host, hint) => PlayVideoInHost(host, hint, v.ResultVideoPath, v.ThumbnailPath));
            border.MouseLeftButtonUp += async (_, _) =>
            {
                try { await _vm.SelectAsync(shot.OrderIndex, v.Id); }
                catch (Exception ex) { ShowError("没能选中这个版本，请再点一次", ex); }
            };
        }
        stack.Children.Add(border);

        // 提示词原文（单行截断）
        stack.Children.Add(new TextBlock
        {
            Text = v.Prompt, FontSize = 10, Foreground = Brush("TextTertiaryBrush"), Width = 90,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0),
            ToolTip = v.Prompt,
        });
        // 完成态：同原长
        if (v.Status == ShotVariantStatus.Completed)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"{_vm.DurationOf(shot):F1}s · 同原长", FontSize = 10,
                Foreground = Brush("SuccessGreenBrush"),
            });
        }
        // 按钮矩阵（对齐 macOS variantActions）：按状态给不同的操作。
        // - TimedOut          →「重试」＝用旧 taskId 续查（不计费）
        // - Failed 且无 taskId →「重试」＝重新提交（之前没提交成功、没扣费）
        // - Failed 且有 taskId →「重新生成」＝新任务，弹计费二次确认
        // - Completed / 正常   → 无（仅删除）
        if (!busy)
        {
            var action = BuildVariantActionButton(v);
            if (action is not null) stack.Children.Add(action);
        }

        // 删除按钮（生成中禁用）
        var del = new Button
        {
            Content = "🗑 删除", FontSize = 10, Margin = new Thickness(0, 2, 0, 0), Padding = new Thickness(4, 1, 4, 1),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), IsEnabled = !busy,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD3, 0x3A, 0x3A)), Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        del.Click += async (_, _) =>
        {
            try { await _vm.DeleteVariantAsync(v.Id); }
            catch (Exception ex) { ShowError("没能删除这个版本，它还在列表里。若它正在生成中，请等出结果后再删", ex); }
        };
        stack.Children.Add(del);
        return stack;
    }

    /// <summary>按变体状态构造操作按钮（重试 / 重新生成 + 计费二次确认）；无需操作返回 null。</summary>
    private Button? BuildVariantActionButton(ShotVariant v)
    {
        string text, tip;
        Func<Task> action;

        if (v.Status == ShotVariantStatus.TimedOut)
        {
            text = "↻ 重试";
            tip = "重新获取这次任务的结果，不会重复扣费";
            action = () => _vm.RetryFetchAsync(v.Id);
        }
        else if (v.Status == ShotVariantStatus.Failed && string.IsNullOrEmpty(v.TaskId))
        {
            // 提交阶段就失败了、没扣过费 → 重试＝重新提交。
            text = "↻ 重试";
            tip = "上次没提交成功、未扣费，点此重新发起";
            action = () => _vm.RegenerateAsync(v.Id);
        }
        else if (v.Status == ShotVariantStatus.Failed) // 有 taskId：阿里 FAILED / 结果过期，旧任务已废
        {
            text = "⟳ 重新生成";
            tip = "会发起一次新任务并按次计费";
            action = async () =>
            {
                // 花钱的操作用系统灰框确认，等于在最需要信任感的时刻抽走信任感 —— 走自绘对话框。
                var reason = string.IsNullOrWhiteSpace(v.FriendlyError)
                    ? string.Empty
                    : $"\n\n上次失败原因：{v.FriendlyError}";
                var confirmed = Shared.MixCutDialog.Confirm(
                    this,
                    "重新生成这个画面变体？",
                    $"会向 AI 发起一次新任务并按次计费（原任务的结果无法找回）。{reason}",
                    confirmText: "生成并计费", cancelText: "暂不生成", destructive: false, icon: "💳");
                if (!confirmed) return;
                await _vm.RegenerateAsync(v.Id);
            };
        }
        else
        {
            return null; // Completed / 正常态：只保留删除
        }

        var btn = new Button
        {
            Content = text, FontSize = 10, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(4, 1, 4, 1),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Brush("AccentBlueBrush"), Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left, ToolTip = tip,
        };
        btn.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { ShowError("操作没成功。若是网络问题稍后再试即可，本次不会重复扣费", ex); }
        };
        return btn;
    }

    private UIElement BuildPromptArea(PhysicalShot shot)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        // 「提示词」标签（左） + 预设下拉（右），两列对齐同一行。
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lbl = new TextBlock
        {
            Text = "提示词", FontSize = 11, Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        topRow.Children.Add(lbl);

        // 预设下拉：常显「预设模板…」占位，选完自动填入提示词并回到占位（像菜单按钮）。Focusable=false 去掉虚线焦点框。
        var presetCombo = new ComboBox
        {
            Width = 200, FontSize = 11, Focusable = false, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(presetCombo, 1);
        presetCombo.Items.Add(new ComboBoxItem { Content = "预设模板…" });   // index 0 占位
        foreach (var (group, items) in Presets)
        {
            foreach (var it in items)
            {
                presetCombo.Items.Add(new ComboBoxItem { Content = $"[{group}] {Shorten(it)}", Tag = it, ToolTip = it });
            }
        }
        presetCombo.SelectedIndex = 0;
        presetCombo.SelectionChanged += (_, _) =>
        {
            if (presetCombo.SelectedItem is ComboBoxItem { Tag: string t } && _promptBox != null)
            {
                _promptBox.Text = t;
                presetCombo.SelectedIndex = 0;   // 回到占位
            }
        };
        topRow.Children.Add(presetCombo);
        panel.Children.Add(topRow);

        _promptBox = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 90,
            Margin = new Thickness(0, 6, 0, 0), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        panel.Children.Add(_promptBox);

        async Task DoGenerateAsync()
        {
            var prompt = _promptBox?.Text?.Trim() ?? string.Empty;
            if (prompt.Length == 0) { ShowError("请先输入提示词"); return; }
            if (_promptBox != null) _promptBox.Text = string.Empty;
            try { await _vm.GenerateVariantAsync(shot.Id, prompt); }
            catch (Exception ex) { ShowError("没能提交这次 AI 生成，本次未扣费。可能是网络不通或 API Key 额度不足，请到「设置 → AI 模型」确认额度", ex); }
        }
        // Ctrl+Enter 快速生成（Enter 仍是换行）。
        _promptBox.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter
                && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                await DoGenerateAsync();
            }
        };

        var genBtn = new Button
        {
            Content = "✨ 生成变体", FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 6, 12, 6),
            Background = Brush("AccentBlueBrush"), Foreground = Brushes.White, BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "提交 AI 生成（约 2~4 分钟，按次计费）。快捷键 Ctrl+Enter",
            IsEnabled = false,   // 提示词为空时禁用（不再点了才报错）
        };
        _promptBox.TextChanged += (_, _) => genBtn.IsEnabled = _promptBox.Text.Trim().Length > 0;
        genBtn.Click += async (_, _) => await DoGenerateAsync();
        panel.Children.Add(genBtn);

        panel.Children.Add(new TextBlock
        {
            Text = "约 2~4 分钟生成，按次计费。同类替换（可乐↔雪碧、换背景、换颜色）效果最好。",
            FontSize = 10, Foreground = Brush("TextTertiaryBrush"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        return panel;
    }

    // ---- 顶栏按钮 ----

    private async void OnComposeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var ok = await _vm.ComposeAsync();
            if (ok)
            {
                Components.ToastService.Show("已合成替换画面", Components.ToastStyle.Success);
                Close();
            }
        }
        catch (Exception ex) { ShowError("合成没能完成，原分镜画面未被改动。可能是内存不足或素材规格过高，建议关闭剪映、浏览器等占内存的程序后重试", ex); }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>分镜头轨道是横向排列，鼠标滚轮应横向滚动（默认竖滚对无竖向内容的轨道无效）。</summary>
    private void OnTrackWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        TrackScroll.ScrollToHorizontalOffset(TrackScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // ---- 工具 ----


    private Border StatusThumb(string text, Color bg, Brush fg)
    {
        return new Border
        {
            Width = 90, Height = 160, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(bg),
            Child = new TextBlock
            {
                Text = text, Foreground = fg, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            },
        };
    }

    /// <summary>缩略图未就绪时的 loading 遮罩（半透明黑底 + 无限进度条 + 文案），替代纯黑。</summary>
    private Border MakeLoadingOverlay(string text)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new ProgressBar
        {
            IsIndeterminate = true, Width = 56, Height = 4, Foreground = Brushes.White,
        });
        sp.Children.Add(new TextBlock
        {
            Text = text, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
        });
        return new Border { Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), Child = sp };
    }

    /// <summary>居中 ▶ 播放提示（圆形半透明底）。</summary>
    private static Border MakePlayHint(double size) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
        Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Cursor = System.Windows.Input.Cursors.Hand,
        Child = new TextBlock
        {
            Text = "▶", Foreground = Brushes.White, FontSize = size * 0.42,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(size * 0.08, 0, 0, 0),
        },
    };

    /// <summary>
    /// 在卡片 host 里懒挂 <see cref="Components.InlineVideoPlayer"/> 起播。复用分镜库同款帧泵：
    /// 全局唯一播放、PrimeDecodeSize 防竖屏黑帧、Idle 播完/被抢占还原缩略图 + ▶。<paramref name="start"/> 决定播什么。
    /// </summary>
    private void MountPlayer(Grid host, Border? playHint, int fallbackW, int fallbackH,
        Action<Components.InlineVideoPlayer> start)
    {
        // 已在播这张卡 → 防抖忽略
        if (host.Children.OfType<Components.InlineVideoPlayer>().Any(p => p.IsPlaying)) return;

        var player = new Components.InlineVideoPlayer
        {
            AutoPlayOnHover = false,
            VideoStretch = Stretch.UniformToFill,
        };
        player.Idle += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (host.Children.Contains(player)) host.Children.Remove(player);
            if (playHint is not null) playHint.Visibility = Visibility.Visible;
        }), System.Windows.Threading.DispatcherPriority.Background);

        if (playHint is not null) playHint.Visibility = Visibility.Collapsed;
        host.Children.Add(player);
        System.Windows.Controls.Panel.SetZIndex(player, 5);
        // 用已布局的 host 尺寸预置解码目标（竖屏框），避免刚 new 出来 ActualWidth=0 → 解码退化成小横帧全黑。
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(host);
        var wpx = (int)Math.Round((host.ActualWidth > 0 ? host.ActualWidth : fallbackW) * dpi.DpiScaleX);
        var hpx = (int)Math.Round((host.ActualHeight > 0 ? host.ActualHeight : fallbackH) * dpi.DpiScaleY);
        player.PrimeDecodeSize(wpx, hpx);
        start(player);
    }

    /// <summary>播放某镜头的源片段（帧精确 [startFrame, endFrame)）。</summary>
    private void PlayShotInHost(Grid host, Border? playHint, string? thumbPath, int startFrame, int endFrame)
    {
        if (string.IsNullOrEmpty(_vm.SourceVideoPath) || _vm.Fps <= 0) return;
        MountPlayer(host, playHint, 96, 170,
            p => p.PlaySegment(_vm.SourceVideoPath, thumbPath, startFrame, endFrame, _vm.Fps));
    }

    /// <summary>播放一个独立视频文件整段（AI 变体结果片）。</summary>
    private void PlayVideoInHost(Grid host, Border? playHint, string? videoPath, string? thumbPath)
    {
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath)) return;
        MountPlayer(host, playHint, 90, 160, p =>
        {
            p.SetVideo(videoPath, thumbPath);
            p.Play();
        });
    }

    /// <summary>版本区缩略图卡（90×160）+ 可选 ▶ 播放。</summary>
    private Border VersionThumb(string? thumbPath, bool selected, Action<Grid, Border>? onPlay)
    {
        var border = new Border
        {
            Width = 90, Height = 160, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            BorderBrush = selected ? Brush("AccentBlueBrush") : Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 2.5 : 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var host = new Grid();
        var img = LoadImage(thumbPath, 90);
        if (img != null) host.Children.Add(new Image { Source = img, Stretch = Stretch.UniformToFill });
        if (onPlay is not null && img != null)
        {
            var hint = MakePlayHint(30);
            hint.MouseLeftButtonUp += (_, e) => { e.Handled = true; onPlay(host, hint); };
            host.Children.Add(hint);
        }
        border.Child = host;
        // hover 微缩放反馈（与镜头卡一致）。
        border.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        border.RenderTransform = scale;
        border.MouseEnter += (_, _) => { scale.ScaleX = scale.ScaleY = 1.04; };
        border.MouseLeave += (_, _) => { scale.ScaleX = scale.ScaleY = 1.0; };
        return border;
    }

    private static BitmapImage? LoadImage(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = decodeWidth * 2;   // 2× 供高 DPI 清晰
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static string Shorten(string s) => s.Length <= 14 ? s : s[..14] + "…";

    private Brush Brush(string key) =>
        (Brush)(Application.Current.TryFindResource(key) ?? Brushes.Gray);
}
