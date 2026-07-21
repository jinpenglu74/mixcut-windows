using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MixCut.Models;
using MixCut.Services.Captions;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>
/// #15 逐句字幕时间编辑器（弹窗，480×600）。文本只读、只调时间；点句播对应配音段；可手动拆句 + 一键重新对齐。
/// 对应 macOS CaptionTimingEditorSheet + Windows 增强（拆分）。每次改动即时写库。
/// </summary>
public sealed class CaptionTimingEditorWindow : Window
{
    // ---- 配色（与主应用一致的干净浅色）----
    private static readonly Brush Bg = New(0xF5, 0xF6, 0xF8);
    private static readonly Brush CardBg = Brushes.White;
    private static readonly Brush CardBorder = New(0xEA, 0xEA, 0xEE);
    private static readonly Brush TextPrimary = New(0x1A, 0x1A, 0x1A);
    private static readonly Brush TextSecondary = New(0x8A, 0x8A, 0x8E);
    private static readonly Brush Accent = New(0x1D, 0x6B, 0xE5);
    private static readonly Brush Green = New(0x2E, 0x8B, 0x57);
    private static readonly Brush Red = New(0xD3, 0x3A, 0x3A);
    private static readonly Brush StepBg = New(0xF1, 0xF2, 0xF4);
    private static readonly Brush StepHover = New(0xE4, 0xE6, 0xEA);
    private static SolidColorBrush New(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static SolidColorBrush Tint(SolidColorBrush b, byte a) => new(Color.FromArgb(a, b.Color.R, b.Color.G, b.Color.B));

    private readonly Guid _dubId;
    private readonly int _variantIndex;
    private readonly double _segDuration;
    private readonly string _audioPath;
    private readonly DubbingViewModel _dubVM;

    private const double Step = 0.1;
    private const double MinGap = 0.05;

    private List<CaptionLine> _lines = new();
    private readonly StackPanel _rows = new();
    private readonly TextBlock _emptyText;
    private readonly MediaPlayer _player = new();
    private int? _playingIndex;
    private DispatcherTimer? _stopTimer;
    private bool _busyRealign;

    public CaptionTimingEditorWindow(Guid dubId, int variantIndex, double segDuration, string audioPath, DubbingViewModel dubVM)
    {
        _dubId = dubId;
        _variantIndex = variantIndex;
        _segDuration = segDuration;
        _audioPath = audioPath;
        _dubVM = dubVM;

        Title = "逐句字幕时间";
        Width = 480;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);   // 中文小字清晰
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

        try { _player.Open(new Uri(_audioPath)); } catch { }

        var root = new DockPanel();
        var header = BuildHeader();
        var footer = BuildFooter();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);

        _emptyText = new TextBlock
        {
            FontSize = 13, Foreground = TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var listHost = new Grid();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 14, 16, 14) };
        scroll.Content = _rows;
        listHost.Children.Add(scroll);
        listHost.Children.Add(_emptyText);
        root.Children.Add(listHost);
        Content = root;

        Loaded += async (_, _) =>
        {
            try { _lines = await _dubVM.LoadCaptionLinesAsync(_dubId); }
            catch (Exception ex)
            {
                // 不能把「读失败」伪装成「没有数据」：空列表会让界面显示
                // 「还没有逐句字幕数据，点下方『重新自动对齐』生成」，
                // 用户照做就会用重新对齐**覆盖掉库里原本正确的数据**。
                Serilog.Log.Error(ex, "[Caption] 读取逐句字幕失败 dub={Dub}", _dubId);
                _lines = new List<CaptionLine>();
                _loadFailed = true;
            }
            RebuildRows();
        };
        Closed += (_, _) => { StopPlayback(); try { _player.Close(); } catch { } };
    }

    // ---- Header ----
    private FrameworkElement BuildHeader()
    {
        var grid = new Grid { Margin = new Thickness(18, 16, 16, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = Tint((SolidColorBrush)Accent, 0x1F),
            Child = new TextBlock { Text = "💬", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "逐句字幕时间", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = TextPrimary });
        titles.Children.Add(new TextBlock
        {
            Text = $"改写版 {(char)('A' + _variantIndex)} · 分镜时长 {_segDuration:F1}s",
            FontSize = 11.5, Foreground = TextSecondary, Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);

        var close = MakeButton("✕", StepBg, TextSecondary, () => Close(), 8, new Thickness(9, 5, 9, 5), 13);
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        return new Border { Child = grid, Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    // ---- Footer ----
    private FrameworkElement BuildFooter()
    {
        var grid = new Grid { Margin = new Thickness(16, 12, 16, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var realign = MakeButton("✨ 重新自动对齐", Accent, Brushes.White, OnRealign, 8, new Thickness(14, 8, 14, 8), 12.5, FontWeights.SemiBold);
        realign.ToolTip = "用配音重新识别每句时间，覆盖手动调过的值（文本不变）";
        Grid.SetColumn(realign, 0);
        grid.Children.Add(realign);

        var hint = new TextBlock
        {
            Text = "字幕文字要改？去改这版台词（会重新配音并重新对齐）",
            FontSize = 10.5, Foreground = TextSecondary, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(hint, 1);
        grid.Children.Add(hint);

        return new Border { Child = grid, Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    private void RebuildRows()
    {
        _rows.Children.Clear();
        // 三种空态要分清楚 —— 尤其「读取失败」绝不能说成「还没有数据」，
        // 否则用户会去点「重新自动对齐」，把库里原本正确的时间数据覆盖掉。
        _emptyText.Text = _busyRealign
            ? "正在对齐…"
            : _loadFailed
                ? "没能读出这版配音的字幕时间数据，请关掉窗口重新打开。\n（先不要点「重新自动对齐」，以免覆盖已有数据）"
                : "还没有逐句字幕数据，点下方「重新自动对齐」生成";
        _emptyText.Visibility = _lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _lines.Count; i++)
            _rows.Children.Add(BuildRow(i));
    }

    // ---- 每句卡片 ----
    private FrameworkElement BuildRow(int i)
    {
        var line = _lines[i];
        var playing = _playingIndex == i;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 播放圆钮
        var play = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Cursor = Cursors.Hand,
            Background = playing ? Tint((SolidColorBrush)Red, 0x22) : Tint((SolidColorBrush)Green, 0x22),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0),
            Child = new TextBlock
            {
                Text = playing ? "■" : "▶", FontSize = 13, Foreground = playing ? Red : Green,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
            ToolTip = "试听这句配音",
        };
        play.MouseLeftButtonUp += (_, _) => PlayLine(i);
        Grid.SetColumn(play, 0);
        grid.Children.Add(play);

        var body = new StackPanel();

        // 顶行：序号徽章 + 时长
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        topRow.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(5), Background = Tint((SolidColorBrush)Accent, 0x18), Padding = new Thickness(6, 1, 6, 1),
            Child = new TextBlock { Text = $"第 {i + 1} 句", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Accent },
        });
        topRow.Children.Add(new TextBlock
        {
            Text = $"{Math.Max(0, line.End - line.Start):F1}s", FontSize = 10.5, Foreground = TextSecondary,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        });
        body.Children.Add(topRow);

        // 文本
        body.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(line.Text) ? "（空）" : line.Text,
            FontSize = 13.5, LineHeight = 21, Foreground = TextPrimary, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        // 时间步进 + 拆分
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(BuildStepper("起", line.Start, v => SetStart(i, v)));
        controls.Children.Add(new Border { Width = 10 });
        controls.Children.Add(BuildStepper("止", line.End, v => SetEnd(i, v)));
        controls.Children.Add(new Border { Width = 10 });
        var split = MakeButton("✂ 拆分", StepBg, TextSecondary, () => SplitAt(i), 7, new Thickness(9, 4, 9, 4), 11);
        split.VerticalAlignment = VerticalAlignment.Center;
        split.ToolTip = "把这句从中间拆成两句（可反复拆，再各自调时间）";
        controls.Children.Add(split);
        body.Children.Add(controls);

        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        return new Border
        {
            Child = grid, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10),
            CornerRadius = new CornerRadius(10), Background = CardBg,
            BorderBrush = playing ? Tint((SolidColorBrush)Accent, 0x66) : CardBorder,
            BorderThickness = new Thickness(playing ? 1.5 : 1),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 1, Opacity = 0.05, Direction = 270 },
        };
    }

    private FrameworkElement BuildStepper(string label, double value, Action<double> set)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });

        var pill = new Border
        {
            CornerRadius = new CornerRadius(7), Background = StepBg, Padding = new Thickness(2),
            Child = null,
        };
        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        inner.Children.Add(StepGlyph("−", () => set(value - Step)));
        inner.Children.Add(new TextBlock
        {
            Text = $"{value:F1}s", FontFamily = new FontFamily("Consolas"), FontSize = 12.5, Foreground = TextPrimary,
            Width = 42, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        inner.Children.Add(StepGlyph("＋", () => set(value + Step)));
        pill.Child = inner;
        sp.Children.Add(pill);
        return sp;
    }

    private FrameworkElement StepGlyph(string glyph, Action onClick)
    {
        var b = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Background = Brushes.White, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = glyph, FontSize = 13, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        b.MouseEnter += (_, _) => b.Background = New(0xEC, 0xF1, 0xFB);
        b.MouseLeave += (_, _) => b.Background = Brushes.White;
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }

    /// <summary>可点击的圆角「按钮」（Border 实现，带 hover）。</summary>
    private Border MakeButton(string content, Brush bg, Brush fg, Action onClick, double radius, Thickness pad, double fontSize, FontWeight? weight = null)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(radius), Background = bg, Padding = pad, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = content, FontSize = fontSize, Foreground = fg, FontWeight = weight ?? FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center },
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.88;
        b.MouseLeave += (_, _) => b.Opacity = 1.0;
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }

    // ---- 编辑（即时写库）----
    // 「起」= 与上一句的分界；「止」= 与下一句的分界。动分界 → 字按时间在相邻句间迁移/合并（联动）。

    private void SetStart(int i, double v)
    {
        if (i > 0) { MoveBoundary(i - 1, v); return; }
        StopPlayback();
        _lines[0].Start = Math.Min(Math.Max(0, v), _lines[0].End - MinGap);
        Persist(); RebuildRows();
    }

    private void SetEnd(int i, double v)
    {
        if (i + 1 < _lines.Count) { MoveBoundary(i, v); return; }
        StopPlayback();
        _lines[i].End = Math.Min(Math.Max(_lines[i].Start + MinGap, v), _segDuration);
        Persist(); RebuildRows();
    }

    private void MoveBoundary(int i, double t)
    {
        var next = CaptionBoundaryEditor.MoveBoundary(_lines, i, t, MinGap);
        if (next.Count == _lines.Count && next.SequenceEqual(_lines)) return;
        StopPlayback();
        _lines = next;
        Persist(); RebuildRows();
    }

    private void SplitAt(int i)
    {
        var next = CaptionBoundaryEditor.SplitLine(_lines, i);
        if (next.Count == _lines.Count) return; // 单字不可拆
        StopPlayback();
        _lines = next;
        Persist(); RebuildRows();
    }

    /// <summary>
    /// 落库当前时间轴。
    ///
    /// 原本是 `_ = SaveCaptionLinesAsync(...)` 这样的 fire-and-forget —— 异常完全无人接：
    /// 用户调了一整屏的时间点，保存失败了没有任何提示，关掉窗口后改动全没了。
    /// 这是本窗口唯一会**丢用户数据**的地方，必须出声。
    /// </summary>
    private async void Persist()
    {
        try
        {
            await _dubVM.SaveCaptionLinesAsync(_dubId, _lines);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[Caption] 保存逐句字幕失败 dub={Dub}", _dubId);
            Components.ToastService.Show(
                "刚才这次时间调整没有保存成功，请再调一次；若反复失败，关掉窗口重新打开会回到上次保存的状态。",
                Components.ToastStyle.Error);
        }
    }

    /// <summary>读取失败标记：用于把「读不出来」和「本来就没有」两种空态区分开。</summary>
    private bool _loadFailed;

    private async void OnRealign()
    {
        if (_busyRealign) return;
        var ok = Shared.MixCutDialog.Confirm(
            this,
            "重新对齐会覆盖你手调的时间",
            "系统会用配音音频重新识别每一句的起止时间，你手动调过的时间点将被覆盖。\n字幕文本不会改变。",
            confirmText: "重新对齐", cancelText: "取消", destructive: true, icon: "⚠");
        if (!ok) return;
        StopPlayback();
        _busyRealign = true;
        _lines = new List<CaptionLine>();
        RebuildRows();
        try
        {
            await _dubVM.AlignCaptionsAsync(_dubId);
            _lines = await _dubVM.LoadCaptionLinesAsync(_dubId);
            _loadFailed = false;
        }
        catch (Exception ex)
        {
            // 原来这里是空 catch：对齐失败后 _lines 停在上面清空的状态，界面显示
            // 「还没有逐句字幕数据，点下方『重新自动对齐』生成」——
            // 把一次失败伪装成「从来没生成过」，用户会一直点同一个按钮。
            Serilog.Log.Error(ex, "[Caption] 重新对齐失败 dub={Dub}", _dubId);
            try { _lines = await _dubVM.LoadCaptionLinesAsync(_dubId); }   // 把原来的行读回来，别停在空列表
            catch { _loadFailed = true; }
            Components.ToastService.Show(
                "重新对齐没成功，你原来的时间没有被改动。可能是网络不通或配音音频损坏，请稍后再试一次。",
                Components.ToastStyle.Error);
        }
        finally { _busyRealign = false; RebuildRows(); }
    }

    // ---- 播放某句音频段（播到「止」自动停）----
    private void PlayLine(int i)
    {
        var wasPlaying = _playingIndex == i;
        StopPlayback();
        if (wasPlaying) { RebuildRows(); return; }
        var line = _lines[i];
        try { _player.Position = TimeSpan.FromSeconds(Math.Max(0, line.Start)); _player.Play(); }
        catch { return; }
        _playingIndex = i;
        var dur = Math.Max(0.1, line.End - line.Start);
        _stopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(dur) };
        _stopTimer.Tick += (_, _) => { StopPlayback(); RebuildRows(); };
        _stopTimer.Start();
        RebuildRows();
    }

    private void StopPlayback()
    {
        _stopTimer?.Stop();
        _stopTimer = null;
        try { _player.Pause(); } catch { }
        _playingIndex = null;
    }
}
