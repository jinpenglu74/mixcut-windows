using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.ShotEdit;

/// <summary>AI 变体生成产物（结果视频 + 缩略图路径）。</summary>
public sealed record ShotVariantResult(string ResultVideoPath, string ThumbnailPath);

/// <summary>
/// 分镜头 AI 变体生成编排：帧精确切出该镜头源片段 → 调 <see cref="WanVideoEditClient"/> 生成
/// → 下载结果（URL 24h 过期，立即下载）→ 抽首帧缩略图。对应 macOS ShotVariantService。注册为单例。
/// </summary>
public sealed class ShotVariantService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly FFmpegRunner _ffmpeg;
    private readonly WanVideoEditClient _client;
    private readonly ILogger<ShotVariantService> _logger;

    public ShotVariantService(FFmpegRunner ffmpeg, WanVideoEditClient client, ILogger<ShotVariantService> logger)
    {
        _ffmpeg = ffmpeg;
        _client = client;
        _logger = logger;
    }

    public async Task<ShotVariantResult> GenerateAsync(
        string sourceVideoPath, string videoHash, Guid shotId, Guid variantId,
        int startFrame, int endFrame, double fps, string prompt,
        Action<string>? onStatus = null, CancellationToken ct = default)
    {
        if (fps <= 0) throw new ShotEditException("镜头缺少帧率信息，无法生成变体");

        var clipPath = Path.Combine(Path.GetTempPath(), $"mixcut-shotclip-{variantId:N}.mp4");
        try
        {
            // 1) 帧精确切出该镜头源片段（trim + setpts 防首帧黑屏）。
            onStatus?.Invoke("准备切片");
            await _ffmpeg.CutSegmentFramesAsync(
                new FrameClip(sourceVideoPath, startFrame, endFrame, fps), clipPath, ct);

            // 2) 调 DashScope wan2.7-videoedit 生成。
            var resultUrl = await _client.EditAsync(clipPath, prompt, onStatus, ct);

            // 3) 下载结果（URL 24h 过期，立即下载）。
            onStatus?.Invoke("下载结果");
            var dir = AppPaths.ShotVariantsDirectory(videoHash, shotId);
            var destPath = Path.Combine(dir, $"{variantId:N}.mp4");
            await using (var src = await Http.GetStreamAsync(resultUrl, ct))
            await using (var fs = File.Create(destPath))
            {
                await src.CopyToAsync(fs, ct);
            }

            // 4) 抽首帧缩略图（t=0）。
            var thumbPath = Path.Combine(dir, $"{variantId:N}.jpg");
            try
            {
                await _ffmpeg.GenerateThumbnailAsync(destPath, thumbPath, 0.0, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ShotEditDiag] 变体缩略图生成失败（不阻塞）variant={Variant}", variantId);
                thumbPath = string.Empty;
            }

            _logger.LogInformation("[ShotEditDiag] 变体生成完成 variant={Variant} -> {Path}", variantId, destPath);
            return new ShotVariantResult(destPath, thumbPath);
        }
        finally
        {
            TryDelete(clipPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略临时文件清理失败 */ }
    }
}
