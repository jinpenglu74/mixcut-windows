using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MixCut.Models;
using MixCut.Services.Captions;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>
/// #15 逐句字幕时间编辑器（弹窗，460×560）。文本只读、只调时间；点句播对应配音段；可一键重新自动对齐。
/// 对应 macOS CaptionTimingEditorSheet。每次改时间即时写库（无需保存）。
/// </summary>
public sealed class CaptionTimingEditorWindow : Window
{
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
        Width = 460;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF9));
        UseLayoutRounding = true;

        try { _player.Open(new Uri(_audioPath)); } catch { /* 首帧就绪前无妨 */ }

        var root = new DockPanel();
        root.Children.Add(BuildHeader());   // Top
        root.Children.Add(BuildFooter());   // Bottom（先加，DockPanel 后加的填满）
        DockPanel.SetDock((UIElement)root.Children[0], Dock.Top);
        DockPanel.SetDock((UIElement)root.Children[1], Dock.Bottom);

        _emptyText = new TextBlock
        {
            Text = "还没有逐句字幕数据", FontSize = 13, Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var listHost = new Grid();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14) };
        _rows.Margin = new Thickness(0);
        scroll.Content = _rows;
        listHost.Children.Add(scroll);
        listHost.Children.Add(_emptyText);
        root.Children.Add(listHost);        // Fill
        Content = root;

        Loaded += async (_, _) =>
        {
            try { _lines = await _dubVM.LoadCaptionLinesAsync(_dubId); }
            catch { _lines = new List<CaptionLine>(); }
            RebuildRows();
        };
        Closed += (_, _) => { StopPlayback(); try { _player.Close(); } catch { } };
    }

    private FrameworkElement BuildHeader()
    {
        var panel = new DockPanel { Margin = new Thickness(12), LastChildFill = true };
        var close = new Button { Content = "关闭", Padding = new Thickness(12, 4, 12, 4), Cursor = System.Windows.Input.Cursors.Hand };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        panel.Children.Add(close);

        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "逐句字幕时间", FontSize = 15, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock
        {
            Text = $"改写版 {(char)('A' + _variantIndex)} · 分镜时长 {_segDuration:F1}s",
            FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 0),
        });
        panel.Children.Add(titles);
        return new Border { Child = panel, BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE6)), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private FrameworkElement BuildFooter()
    {
        var panel = new DockPanel { Margin = new Thickness(12), LastChildFill = true };
        var realign = new Button
        {
            Content = "✨ 重新自动对齐", Padding = new Thickness(10, 5, 10, 5), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "用配音重新识别每句时间，覆盖手动调过的值（文本不变）",
        };
        realign.Click += OnRealignClick;
        DockPanel.SetDock(realign, Dock.Left);
        panel.Children.Add(realign);
        panel.Children.Add(new TextBlock
        {
            Text = "字幕文字要改？去改这版台词（会重新配音并重新对齐）",
            FontSize = 10, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        });
        return new Border { Child = panel, BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE6)), BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    private void RebuildRows()
    {
        // 注意：只重绘，不动播放状态（播放的启停由 PlayLine/编辑路径显式管理，否则会把刚开始的播放立刻停掉）。
        _rows.Children.Clear();
        _emptyText.Text = _busyRealign ? "正在对齐…" : "还没有逐句字幕数据";
        _emptyText.Visibility = _lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _lines.Count; i++)
        {
            _rows.Children.Add(BuildRow(i));
        }
    }

    private FrameworkElement BuildRow(int i)
    {
        var line = _lines[i];
        var playing = _playingIndex == i;

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 播放/停止
        var playBtn = new Button
        {
            Content = playing ? "■" : "▶",
            Foreground = playing ? Brushes.Red : new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57)),
            FontSize = 16, Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "试听这句配音", Margin = new Thickness(0, 2, 8, 0),
        };
        playBtn.Click += (_, _) => PlayLine(i);
        Grid.SetColumn(playBtn, 0);
        grid.Children.Add(playBtn);

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(line.Text) ? "（空）" : line.Text,
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });
        var steppers = new StackPanel { Orientation = Orientation.Horizontal };
        steppers.Children.Add(BuildStepper("起", line.Start, v => SetStart(i, v)));
        steppers.Children.Add(new Border { Width = 12 });
        steppers.Children.Add(BuildStepper("止", line.End, v => SetEnd(i, v)));
        body.Children.Add(steppers);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        return new Border
        {
            Child = grid, Padding = new Thickness(10), Margin = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb((byte)(playing ? 0xF2 : 0xFF), 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE6)), BorderThickness = new Thickness(1),
        };
    }

    private FrameworkElement BuildStepper(string label, double value, Action<double> set)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        var minus = new Button { Content = "−", Width = 22, Height = 22, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0) };
        minus.Click += (_, _) => set(value - Step);
        sp.Children.Add(minus);
        sp.Children.Add(new TextBlock
        {
            Text = $"{value:F1}s", FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Width = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        var plus = new Button { Content = "＋", Width = 22, Height = 22, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0) };
        plus.Click += (_, _) => set(value + Step);
        sp.Children.Add(plus);
        return sp;
    }

    // ---- 编辑（即时写库，带约束）----
    // 「起」= 与上一句的分界；「止」= 与下一句的分界。动分界 → 字按时间在相邻句间迁移/合并（联动）。

    private void SetStart(int i, double v)
    {
        if (i > 0) { MoveBoundary(i - 1, v); return; }
        // 首句起：只 clamp 自己 [0, 止-gap]
        StopPlayback();
        _lines[0].Start = Math.Min(Math.Max(0, v), _lines[0].End - MinGap);
        Persist(); RebuildRows();
    }

    private void SetEnd(int i, double v)
    {
        if (i + 1 < _lines.Count) { MoveBoundary(i, v); return; }
        // 末句止：只 clamp 自己 [起+gap, 分镜时长]
        StopPlayback();
        _lines[i].End = Math.Min(Math.Max(_lines[i].Start + MinGap, v), _segDuration);
        Persist(); RebuildRows();
    }

    private void MoveBoundary(int i, double t)
    {
        var next = CaptionBoundaryEditor.MoveBoundary(_lines, i, t, MinGap);
        if (next.Count == _lines.Count && next.SequenceEqual(_lines)) return; // noop
        StopPlayback();
        _lines = next;
        Persist(); RebuildRows();
    }

    private void Persist() => _ = _dubVM.SaveCaptionLinesAsync(_dubId, _lines);

    private async void OnRealignClick(object sender, RoutedEventArgs e)
    {
        if (_busyRealign) return;
        var ok = MessageBox.Show(
            "将用配音重新识别每句时间，覆盖你手动调过的时间。文本不变。\n\n确定重新对齐？",
            "重新自动对齐", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        StopPlayback();
        _busyRealign = true;
        _lines = new List<CaptionLine>();
        RebuildRows();
        try
        {
            await _dubVM.AlignCaptionsAsync(_dubId);
            _lines = await _dubVM.LoadCaptionLinesAsync(_dubId);
        }
        catch { /* 失败保持空，比例兜底已在 AlignCaptions 内 */ }
        finally
        {
            _busyRealign = false;
            RebuildRows();
        }
    }

    // ---- 播放某句音频段（纯音频段，播到「止」自动停）----

    private void PlayLine(int i)
    {
        StopPlayback();
        if (_playingIndex == i) { _playingIndex = null; RebuildRows(); return; }
        var line = _lines[i];
        try
        {
            _player.Position = TimeSpan.FromSeconds(Math.Max(0, line.Start));
            _player.Play();
        }
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
