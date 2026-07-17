using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MixCut.Infrastructure;
using MixCut.Models;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Views;

/// <summary>
/// #18 分镜拆分预览窗口：剪映式帧级 scrub —— 拖手柄画面<b>实时</b>跟到那一帧。
/// 关键：进程外 ffmpeg 每次 seek 都要冷启动（~300ms），逐帧抽根本跟不上拖动。所以改为
/// <b>窗口打开时一次性把整段解码成小尺寸内存帧数组</b>，拖动时按帧号直接取内存帧（瞬时、真跟手）。
/// 手柄范围 clamp 到 [start+min, end-min]（每段至少 ~0.3s），确定后返回拆分帧号。
/// </summary>
public partial class SplitSegmentWindow : Window
{
    // 预览帧尺寸（9:16 竖框；scale+pad 到精确 W×H → 每帧定长，读取端可按定长切帧）。
    private const int PreviewW = 160;
    private const int PreviewH = 284;

    private readonly string? _videoPath;
    private readonly double _fps;          // EffectiveFps（换算秒/帧，与 SplitSegmentAsync 口径一致）
    private readonly int _nominalFps;      // 解码用整数帧率
    private readonly int _startFrame;
    private readonly int _endFrame;

    private BitmapSource[]? _frames;       // 预解码的整段预览帧（index 0 ≈ startFrame）
    private bool _ready;

    /// <summary>用户确定的拆分帧号（DialogResult=true 时有效）。</summary>
    public int CutFrame { get; private set; }

    public SplitSegmentWindow(Segment segment)
    {
        InitializeComponent();
        _videoPath = segment.Video?.LocalPath;
        _fps = segment.EffectiveFps > 0 ? segment.EffectiveFps : 30.0;
        _nominalFps = FrameTime.NominalFps(_fps);
        _startFrame = segment.StartFrame;
        _endFrame = segment.EndFrame;

        var minFrames = Math.Max(1, (int)Math.Round(0.3 * _fps));
        var lo = _startFrame + minFrames;
        var hi = _endFrame - minFrames;
        if (hi < lo || string.IsNullOrEmpty(_videoPath) || !File.Exists(_videoPath))
        {
            ConfirmButton.IsEnabled = false;
            FrameSlider.IsEnabled = false;
            InfoText.Text = string.IsNullOrEmpty(_videoPath) || !File.Exists(_videoPath ?? string.Empty)
                ? "找不到源视频文件" : "分镜太短，无法拆分（每段至少 0.3 秒）";
            return;
        }

        FrameSlider.Minimum = lo;
        FrameSlider.Maximum = hi;
        FrameSlider.Value = Math.Clamp((_startFrame + _endFrame) / 2, lo, hi);
        FrameSlider.IsEnabled = false;    // 预解码完成才启用
        InfoText.Text = "加载预览帧 …";
        FrameSlider.ValueChanged += (_, _) => ShowFrameForSlider();

        Loaded += async (_, _) => await PreloadFramesAsync();
    }

    /// <summary>窗口打开时一次性把整段解码成小尺寸预览帧存内存（之后拖动瞬时取帧，真跟手）。</summary>
    private async System.Threading.Tasks.Task PreloadFramesAsync()
    {
        try
        {
            var startSec = FrameTime.FrameToSeconds(_startFrame, _fps);
            var durSec = FrameTime.FrameToSeconds(Math.Max(1, _endFrame - _startFrame), _fps);
            var frameBytes = FramePipeArgs.FrameBytes(PreviewW, PreviewH);

            var frames = await System.Threading.Tasks.Task.Run(() =>
            {
                var list = new List<BitmapSource>();
                var psi = new ProcessStartInfo(BundledBinaries.Ffmpeg)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var a in FramePipeArgs.Video(_videoPath!, startSec, durSec, PreviewW, PreviewH, _nominalFps))
                {
                    psi.ArgumentList.Add(a);
                }
                using var proc = Process.Start(psi)!;
                ChildProcessTracker.AddProcess(proc);   // 铁律：防孤儿
                proc.BeginErrorReadLine();              // 丢弃 stderr，避免管道写满阻塞
                var stream = proc.StandardOutput.BaseStream;
                var buf = new byte[frameBytes];
                while (ReadFull(stream, buf, frameBytes))
                {
                    var bmp = BitmapSource.Create(
                        PreviewW, PreviewH, 96, 96, PixelFormats.Bgra32, null, (byte[])buf.Clone(), PreviewW * 4);
                    bmp.Freeze();   // 冻结后可跨线程安全赋给 UI
                    list.Add(bmp);
                    if (list.Count > 2000) break;   // 安全上限（>66s@30fps，分镜不会这么长）
                }
                try { if (!proc.HasExited) proc.WaitForExit(1000); } catch { /* 尽力 */ }
                return list.ToArray();
            });

            _frames = frames;
            _ready = _frames.Length > 0;
            if (!_ready)
            {
                InfoText.Text = "预览加载失败";
                return;
            }
            Serilog.Log.Information("[SplitDiag] 预览帧就绪 frames={N} range=[{S},{E})", _frames.Length, _startFrame, _endFrame);
            FrameSlider.IsEnabled = true;
            ShowFrameForSlider();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[SplitDiag] 预览帧预解码失败");
            InfoText.Text = "预览加载失败";
        }
    }

    /// <summary>滑块变化 → 从内存帧数组按帧号瞬时取帧显示。</summary>
    private void ShowFrameForSlider()
    {
        if (!_ready || _frames is null) return;
        var frame = (int)Math.Round(FrameSlider.Value);
        var timeSec = FrameTime.FrameToSeconds(frame, _fps);
        InfoText.Text = $"拆分点：{timeSec:F1}s · 第 {frame} 帧";
        // 解码从 startFrame、按 nominalFps 采样 → 第 k 帧对应源帧 startFrame + k*(fps/nominalFps)。
        var idx = (int)Math.Round((frame - _startFrame) * (double)_nominalFps / _fps);
        idx = Math.Clamp(idx, 0, _frames.Length - 1);
        PreviewImage.Source = _frames[idx];
    }

    private static bool ReadFull(Stream s, byte[] buf, int count)
    {
        var off = 0;
        while (off < count)
        {
            var n = s.Read(buf, off, count - off);
            if (n <= 0) return false;
            off += n;
        }
        return true;
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
