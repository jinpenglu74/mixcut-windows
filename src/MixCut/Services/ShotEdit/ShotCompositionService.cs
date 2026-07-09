using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using MixCut.Infrastructure;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.ShotEdit;

/// <summary>合成时一个镜头位置的输入。VariantVideoPath 为空 = 用原镜头切片。对应 mac SlotInput。</summary>
public readonly record struct ShotSlotInput(int StartFrame, int EndFrame, string? VariantVideoPath);

/// <summary>合成结果：替换画面 mp4 + 实际帧数 + 时长。对应 mac ShotCompositionService.Result。</summary>
public sealed record ShotCompositionResult(string CompositeVideoPath, int TotalFrames, double Duration);

/// <summary>
/// 分镜头合成服务：把每个镜头位置选定的版本（原切片 / AI 变体）规整到精确帧数 → concat 拼接（720:1280）
/// → 复用原分镜整段音频混流 → 落临时目录。对应 macOS ShotCompositionService。注册为单例。
/// </summary>
public sealed class ShotCompositionService
{
    /// <summary>合成分辨率（scale 用冒号语法，铁律；对齐 mac 720:1280）。</summary>
    public const string Resolution = "720:1280";

    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<ShotCompositionService> _logger;

    public ShotCompositionService(FFmpegRunner ffmpeg, ILogger<ShotCompositionService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>
    /// 合成：<paramref name="slots"/> 按 orderIndex 升序，逐坑取原切片或变体片规整到精确帧数，
    /// concat 到 720:1280，再把 [segmentStart, segmentEnd] 原音频混回去。返回临时合成片。
    /// </summary>
    public async Task<ShotCompositionResult> ComposeAsync(
        string sourceVideoPath, double fps, IReadOnlyList<ShotSlotInput> slots,
        double segmentStart, double segmentEnd, CancellationToken ct = default)
    {
        if (fps <= 0 || slots.Count == 0)
        {
            throw new ShotEditException("合成输入无效（缺帧率或镜头为空）");
        }

        var workDir = Path.Combine(Path.GetTempPath(), $"mixcut-shotcompose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            // 1) 逐坑产出精确帧数切片。
            var clips = new List<FrameClip>();
            var totalFrames = 0;
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                var targetFrames = Math.Max(1, slot.EndFrame - slot.StartFrame);
                totalFrames += targetFrames;
                var slotPath = Path.Combine(workDir, $"slot-{i}.mp4");

                if (!string.IsNullOrEmpty(slot.VariantVideoPath) && File.Exists(slot.VariantVideoPath))
                {
                    await NormalizeVariantAsync(slot.VariantVideoPath!, targetFrames, fps, slotPath, ct);
                }
                else
                {
                    await _ffmpeg.CutSegmentFramesAsync(
                        new FrameClip(sourceVideoPath, slot.StartFrame, slot.EndFrame, fps), slotPath, ct);
                }
                // 切片自身帧号从 0 起，取满 targetFrames；concat 内部再统一到 720:1280 / 30fps。
                clips.Add(new FrameClip(slotPath, 0, targetFrames, fps));
            }

            // 2) concat 拼接（统一 720:1280 + 30fps + yuv420p，与导出同源）。
            var joined = Path.Combine(workDir, "joined.mp4");
            await _ffmpeg.ConcatAsync(clips, joined, resolution: Resolution, cancellationToken: ct);

            // 3) 取原分镜整段音频（复用原音频，防音画漂移）。
            var hasAudio = await _ffmpeg.ProbeHasAudioAsync(sourceVideoPath, ct);
            string? audioPath = null;
            if (hasAudio)
            {
                audioPath = Path.Combine(workDir, "audio.m4a");
                await _ffmpeg.RunAsync(new[]
                {
                    "-ss", Fmt(segmentStart), "-to", Fmt(segmentEnd), "-i", sourceVideoPath,
                    "-vn", "-c:a", "aac", "-b:a", "192k", "-y", audioPath,
                }, cancellationToken: ct);
            }

            // 4) mux：画面 copy 不重编码，只换音轨。
            var composite = Path.Combine(Path.GetTempPath(), $"mixcut-composite-{Guid.NewGuid():N}.mp4");
            var muxArgs = new List<string> { "-i", joined };
            if (audioPath is not null) muxArgs.AddRange(new[] { "-i", audioPath });
            muxArgs.AddRange(new[] { "-map", "0:v:0" });
            if (audioPath is not null) muxArgs.AddRange(new[] { "-map", "1:a:0", "-c:a", "aac" });
            muxArgs.AddRange(new[] { "-c:v", "copy", "-shortest", "-movflags", "+faststart", "-y", composite });
            await _ffmpeg.RunAsync(muxArgs, cancellationToken: ct);

            // 5) 探测实际帧数（concat 统一 30fps 后帧数 ≠ 源 fps 累加值）。
            var actualFrames = await ProbeFrameCountAsync(composite, ct) ?? totalFrames;
            var duration = FrameTime.FrameToSeconds(totalFrames, fps);
            _logger.LogInformation(
                "[ShotComposeDiag] slots={Slots} totalFrames(src)={Total} actualFrames={Actual} dur={Dur:F2}s -> {Path}",
                slots.Count, totalFrames, actualFrames, duration, composite);
            return new ShotCompositionResult(composite, actualFrames, duration);
        }
        finally
        {
            TryDeleteDir(workDir);
        }
    }

    /// <summary>把变体片规整到精确帧数（多退少补：trim 截断 / tpad 克隆末帧补齐），重编码统一像素格式。</summary>
    private async Task NormalizeVariantAsync(
        string variantPath, int targetFrames, double fps, string outputPath, CancellationToken ct)
    {
        var actual = await ProbeFrameCountAsync(variantPath, ct) ?? targetFrames;
        string vf;
        if (actual == targetFrames)
        {
            vf = "setpts=PTS-STARTPTS";
        }
        else if (actual > targetFrames)
        {
            vf = $"trim=end_frame={targetFrames},setpts=PTS-STARTPTS";
        }
        else
        {
            var stopDur = (targetFrames - actual) / fps;
            vf = $"tpad=stop_mode=clone:stop_duration={Fmt(stopDur)},setpts=PTS-STARTPTS";
        }

        var codec = HardwareEncoderProbe.H264Hardware ?? "libx264";
        var isHardware = HardwareEncoderProbe.H264Hardware is not null;
        var args = new List<string> { "-i", variantPath, "-vf", vf, "-an", "-c:v", codec };
        if (isHardware) args.AddRange(new[] { "-b:v", "8000k", "-maxrate", "16000k", "-tag:v", "avc1" });
        else args.AddRange(new[] { "-crf", "18", "-preset", "fast" });
        args.AddRange(new[] { "-pix_fmt", "yuv420p", "-y", outputPath });

        try
        {
            await _ffmpeg.RunAsync(args, cancellationToken: ct);
        }
        catch (Exception ex) when (isHardware)
        {
            _logger.LogWarning(ex, "[ShotComposeDiag] 变体规整硬件编码失败，降级 CPU 重试");
            var soft = new List<string> { "-i", variantPath, "-vf", vf, "-an", "-c:v", "libx264",
                "-crf", "18", "-preset", "fast", "-pix_fmt", "yuv420p", "-y", outputPath };
            await _ffmpeg.RunAsync(soft, cancellationToken: ct);
        }
    }

    /// <summary>ffprobe -count_frames 精确统计视频帧数（解码计数，慢但准）。失败返回 null。</summary>
    public async Task<int?> ProbeFrameCountAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var stdout = await _ffmpeg.RunProbeAsync(new[]
            {
                "-v", "error", "-count_frames", "-select_streams", "v:0",
                "-show_entries", "stream=nb_read_frames", "-of", "default=nokey=1:noprint_wrappers=1", path,
            }, timeoutSeconds: 120, ct);
            var line = stdout.Trim();
            return int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Fmt(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 忽略临时目录清理失败 */ }
    }
}
