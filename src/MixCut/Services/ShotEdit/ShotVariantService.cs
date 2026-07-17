using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using MixCut.Services.AI;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.ShotEdit;

/// <summary>AI 变体生成的终局。对应 macOS ShotVariantService 的 Outcome。</summary>
public enum VariantOutcome
{
    /// <summary>查到 SUCCEEDED、结果已下载并抽好缩略图。</summary>
    Completed,
    /// <summary>阿里返回 FAILED / 下载失败等，<c>FailReason</c> 为人话原因（有 taskId，重新生成才计费）。</summary>
    Failed,
    /// <summary>结果过期（task_status=UNKNOWN），旧成片拿不回，需重新生成（会计费）。</summary>
    Expired,
    /// <summary>本地轮询到点仍无终局，云端可能还在跑；可用同 taskId「重试」（不重复扣费）。</summary>
    TimedOut,
}

/// <summary>一次生成/续查的终局结果。</summary>
public readonly record struct VariantResult(
    VariantOutcome Outcome, string? ResultVideoPath, string? ThumbnailPath, string? FailReason);

/// <summary>
/// 分镜头 AI 变体生成编排：帧精确切片 → <see cref="WanVideoEditClient.SubmitAsync"/> 拿 taskId
/// → 回调调用方立刻落库 taskId（防超时丢失）→ 轮询到终局 → 成功则下载结果（URL 24h 过期，立即下）+ 抽首帧。
/// 轮询上限在这里（20 分钟），到点转 <see cref="VariantOutcome.TimedOut"/> 而非当失败。
/// 对应 macOS ShotVariantService。注册为单例。
/// </summary>
public sealed class ShotVariantService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>轮询间隔（秒）。</summary>
    private const int PollIntervalSeconds = 15;
    /// <summary>最大轮询次数（80 × 15s = 20 分钟）。到点转 TimedOut，比原来 10 分钟宽，减少「云端还在跑却被判超时」。</summary>
    private const int MaxPolls = 80;

    private readonly FFmpegRunner _ffmpeg;
    private readonly WanVideoEditClient _client;
    private readonly ILogger<ShotVariantService> _logger;

    public ShotVariantService(FFmpegRunner ffmpeg, WanVideoEditClient client, ILogger<ShotVariantService> logger)
    {
        _ffmpeg = ffmpeg;
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// 首次生成：切片 → 提交 → <paramref name="onTaskCreated"/>（调用方立刻落库 taskId）→ 轮询到终局。
    /// <b>只有「提交阶段失败」（切片/提交抛错）才 throw</b>——这类没拿到 taskId、没扣费，调用方 catch 后置
    /// Failed(TaskId=null)。拿到 taskId 之后一律以 <see cref="VariantResult"/> 返回（不再 throw），
    /// 避免把「云端可能成功且已扣费」误判成「没扣费、可重提」。
    /// </summary>
    public async Task<VariantResult> GenerateAsync(
        string sourceVideoPath, string videoHash, Guid shotId, Guid variantId,
        int startFrame, int endFrame, double fps, string prompt,
        Func<string, Task> onTaskCreated,
        Action<string>? onStatus = null, CancellationToken ct = default)
    {
        if (fps <= 0) throw new ShotEditException("镜头缺少帧率信息，无法生成变体");

        var clipPath = Path.Combine(Path.GetTempPath(), $"mixcut-shotclip-{variantId:N}.mp4");
        string taskId;
        try
        {
            // 1) 帧精确切出该镜头源片段（trim + setpts 防首帧黑屏）。
            onStatus?.Invoke("准备切片");
            await _ffmpeg.CutSegmentFramesAsync(
                new FrameClip(sourceVideoPath, startFrame, endFrame, fps), clipPath, ct);

            // 2) 提交（内联 Base64 media），拿 taskId。—— 这两步的失败属「提交阶段失败」，往外抛。
            onStatus?.Invoke("上传中");
            taskId = await _client.SubmitAsync(clipPath, prompt, ct);
        }
        finally
        {
            TryDelete(clipPath);   // 已内联上传，提交后即可删本地切片
        }

        // 3) 提交成功 —— 立刻落库 taskId（防本地超时后丢失、无法查回）。此后一律返回 VariantResult。
        await onTaskCreated(taskId);
        return await PollToOutcomeAsync(taskId, videoHash, shotId, variantId, onStatus, ct);
    }

    /// <summary>超时重试：跳过切片与提交，用已落库的 taskId 直接轮询到终局（不重新提交、不重复扣费）。</summary>
    public Task<VariantResult> ResumeAsync(
        string taskId, string videoHash, Guid shotId, Guid variantId,
        Action<string>? onStatus = null, CancellationToken ct = default)
        => PollToOutcomeAsync(taskId, videoHash, shotId, variantId, onStatus, ct);

    private async Task<VariantResult> PollToOutcomeAsync(
        string taskId, string videoHash, Guid shotId, Guid variantId,
        Action<string>? onStatus, CancellationToken ct)
    {
        onStatus?.Invoke("生成中");
        for (var i = 0; i < MaxPolls; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct);

            var poll = await _client.PollOnceAsync(taskId, ct);
            switch (poll.Outcome)
            {
                case PollOutcome.Succeeded:
                    onStatus?.Invoke("下载结果");
                    try
                    {
                        var (dest, thumb) = await DownloadAndThumbAsync(
                            poll.VideoUrl!, videoHash, shotId, variantId, ct);
                        _logger.LogInformation("[ShotEditDiag] 变体生成完成 variant={Variant} -> {Path}", variantId, dest);
                        return new VariantResult(VariantOutcome.Completed, dest, thumb, null);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[ShotEditDiag] 结果下载失败 variant={Variant}", variantId);
                        return new VariantResult(VariantOutcome.Failed, null, null,
                            "结果下载失败：" + ApiErrorClassifier.Friendly(ex));
                    }
                case PollOutcome.Failed:
                    return new VariantResult(VariantOutcome.Failed, null, null, poll.FailReason);
                case PollOutcome.Expired:
                    return new VariantResult(VariantOutcome.Expired, null, null, null);
                case PollOutcome.Running:
                default:
                    onStatus?.Invoke("生成中");
                    break;
            }
        }
        // 轮询到点仍无终局 —— 云端可能还在跑，交给上层显示「已超时」+ 可重试。
        _logger.LogWarning("[ShotEditDiag] 变体本地轮询超时(20min) variant={Variant} taskId={TaskId}", variantId, taskId);
        return new VariantResult(VariantOutcome.TimedOut, null, null, null);
    }

    /// <summary>下载结果视频（24h 过期立即下）+ 抽首帧缩略图（失败不阻塞，缩略图置空）。</summary>
    private async Task<(string dest, string thumb)> DownloadAndThumbAsync(
        string resultUrl, string videoHash, Guid shotId, Guid variantId, CancellationToken ct)
    {
        var dir = AppPaths.ShotVariantsDirectory(videoHash, shotId);
        var destPath = Path.Combine(dir, $"{variantId:N}.mp4");
        await using (var src = await Http.GetStreamAsync(resultUrl, ct))
        await using (var fs = File.Create(destPath))
        {
            await src.CopyToAsync(fs, ct);
        }

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
        return (destPath, thumbPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略临时文件清理失败 */ }
    }
}
