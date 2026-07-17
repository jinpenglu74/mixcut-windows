using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using MixCut.Models;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Views;

/// <summary>
/// #18 分镜拆分预览窗口：帧级 seek 预览（拖手柄→抽该帧显示），确定后返回拆分帧号。
/// 手柄范围 clamp 到 [start+min, end-min]（每段至少 ~0.3s），保证两段都非空。
/// </summary>
public partial class SplitSegmentWindow : Window
{
    private readonly FFmpegRunner _ffmpeg;
    private readonly string? _videoPath;
    private readonly double _fps;
    private readonly int _startFrame;
    private readonly int _endFrame;
    private readonly string _scrubDir;

    private int _lastRenderedFrame = -1;
    private System.Threading.CancellationTokenSource? _seekCts;

    /// <summary>用户确定的拆分帧号（DialogResult=true 时有效）。</summary>
    public int CutFrame { get; private set; }

    public SplitSegmentWindow(Segment segment, FFmpegRunner ffmpeg)
    {
        InitializeComponent();
        _ffmpeg = ffmpeg;
        _videoPath = segment.Video?.LocalPath;
        _fps = segment.EffectiveFps > 0 ? segment.EffectiveFps : 30.0;
        _startFrame = segment.StartFrame;
        _endFrame = segment.EndFrame;
        _scrubDir = Path.Combine(AppPaths.Root, "ScrubCache");
        Directory.CreateDirectory(_scrubDir);

        // 每段至少 minFrames（~0.3s），手柄范围 clamp。
        var minFrames = Math.Max(1, (int)Math.Round(0.3 * _fps));
        var lo = _startFrame + minFrames;
        var hi = _endFrame - minFrames;
        if (hi < lo)
        {
            // 分镜太短、无法切出两段非空 —— 禁用确定，提示。
            ConfirmButton.IsEnabled = false;
            FrameSlider.IsEnabled = false;
            InfoText.Text = "分镜太短，无法拆分（每段至少 0.3 秒）";
            return;
        }

        FrameSlider.Minimum = lo;
        FrameSlider.Maximum = hi;
        var mid = (_startFrame + _endFrame) / 2;
        FrameSlider.Value = Math.Clamp(mid, lo, hi);
        FrameSlider.ValueChanged += (_, _) => OnSliderChanged();

        Loaded += (_, _) => OnSliderChanged();
    }

    private void OnSliderChanged()
    {
        var frame = (int)Math.Round(FrameSlider.Value);
        var timeSec = FrameTime.FrameToSeconds(frame, _fps);
        InfoText.Text = $"拆分点：{timeSec:F1}s · 第 {frame} 帧";
        _ = RenderFrameAsync(frame);
    }

    /// <summary>抽该帧显示（最新优先：连续拖动只让最后一帧落地）。</summary>
    private async System.Threading.Tasks.Task RenderFrameAsync(int frame)
    {
        if (string.IsNullOrEmpty(_videoPath) || !File.Exists(_videoPath)) return;
        if (frame == _lastRenderedFrame) return;

        _seekCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _seekCts = cts;
        var token = cts.Token;
        try
        {
            await System.Threading.Tasks.Task.Delay(120, token);   // debounce 连续拖动
            var outPath = Path.Combine(_scrubDir,
                $"split_{Math.Abs(_videoPath!.GetHashCode())}_{frame}.jpg");
            if (!File.Exists(outPath))
            {
                await _ffmpeg.GenerateThumbnailAsync(_videoPath, outPath,
                    FrameTime.FrameToSeconds(frame, _fps), token);
            }
            if (token.IsCancellationRequested || !File.Exists(outPath)) return;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 360;
            bmp.UriSource = new Uri(outPath);
            bmp.EndInit();
            bmp.Freeze();
            if (!token.IsCancellationRequested)
            {
                PreviewImage.Source = bmp;
                _lastRenderedFrame = frame;
            }
        }
        catch (OperationCanceledException) { /* 被后续拖动取消，正常 */ }
        catch { /* 抽帧失败不影响交互 */ }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        CutFrame = (int)Math.Round(FrameSlider.Value);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
