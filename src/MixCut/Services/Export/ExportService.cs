using System.IO;
using Microsoft.Extensions.Logging;
using MixCut.Models;
using MixCut.Services.VideoProcessing;

namespace MixCut.Services.Export;

/// <summary>导出阶段。对应 macOS 版 ExportProgress.Phase。</summary>
public enum ExportPhase
{
    Cutting,
    Concatenating,
    Encoding,
    Completed,
    Failed,
}

/// <summary>导出进度。对应 macOS 版 ExportProgress。</summary>
public readonly record struct ExportProgress(ExportPhase Phase, double Progress, string Description);

/// <summary>导出任务的输入数据。对应 macOS 版 ExportInput。</summary>
public sealed record ExportInput(
    IReadOnlyList<FrameClip> Segments,
    int MaxWidth,
    int MaxHeight,
    int SkippedCount = 0)
{
    /// <summary>
    /// 从 MixScheme 提取导出数据；无有效分镜时返回 null。
    /// <paramref name="useVocalsAudio"/>（issue #22 BGM 替换模式）：每个分镜的音频改从该视频整轨
    /// 人声分离产物 vocals.wav 按 [Segment.StartTime, Segment.EndTime]（原视频时间轴）切片 ——
    /// 含画面替换的分镜同样用原视频时间轴（见 <see cref="AudioOverride"/> 注释）。
    /// vocals 文件是否存在由 ExportView 预检 + <see cref="ExportService"/> 渲染前校验把关。
    /// </summary>
    public static ExportInput? FromScheme(MixScheme scheme, bool useVocalsAudio = false)
    {
        var ordered = scheme.OrderedSegments;
        if (ordered.Count == 0)
        {
            return null;
        }

        var segments = new List<FrameClip>();
        var skipped = 0;
        var maxWidth = 0;
        var maxHeight = 0;
        foreach (var schemeSeg in ordered)
        {
            var segment = schemeSeg.Segment;
            var video = segment?.Video;
            if (segment is null || video is null)
            {
                skipped++;
                continue;
            }
            // #12：统一走 EffectivePicture（有 AI 替换画面则用替换、否则原源）—— 未替换时返回原值，行为不变。
            var ep = segment.EffectivePicture;
            if (string.IsNullOrEmpty(ep.VideoPath) || !File.Exists(ep.VideoPath))
            {
                skipped++; // 源/替换文件丢失的分镜：统计后由调用方告知用户，不静默少导出
                continue;
            }
            AudioOverride? audio = null;
            if (useVocalsAudio && !string.IsNullOrEmpty(video.ContentHash))
            {
                var vocals = Path.Combine(
                    Utilities.AppPaths.StemsDirectory(video.ContentHash), "vocals.wav");
                audio = new AudioOverride(vocals, segment.StartTime, segment.EndTime);
            }
            segments.Add(new FrameClip(
                ep.VideoPath, ep.StartFrame, ep.EndFrame, ep.Fps > 0 ? ep.Fps : 30, audio));
            maxWidth = Math.Max(maxWidth, video.Width);
            maxHeight = Math.Max(maxHeight, video.Height);
        }

        return segments.Count == 0 ? null : new ExportInput(segments, maxWidth, maxHeight, skipped);
    }
}

/// <summary>导出异常。对应 macOS 版 ExportError。</summary>
public sealed class ExportException : Exception
{
    public ExportException(string message) : base(message)
    {
    }

    public static ExportException NoSegments() => new("方案中没有任何分镜");
    public static ExportException NoValidSegments() => new("方案中没有有效的分镜（可能视频文件不存在）");
    public static ExportException EncodingFailed(string detail) => new($"编码失败: {detail}");
}

/// <summary>视频导出服务。对应 macOS 版 ExportService。注册为单例服务。</summary>
public sealed class ExportService
{
    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<ExportService> _logger;

    public ExportService(FFmpegRunner ffmpeg, ILogger<ExportService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>
    /// 导出混剪方案为 MP4。
    ///
    /// 失败或取消时会**删除写了一半的输出文件**：ffmpeg 中途被杀留下的 mp4 通常仍能双击打开、
    /// 只是内容截断，混在一批成品里用户根本分不出哪个是坏的（批量导出 20 条中途取消尤其致命）。
    /// 宁可什么都没有，也不能留一个看起来正常的残次品。
    /// </summary>
    public async Task ExportAsync(
        ExportInput input,
        string outputPath,
        ExportConfig? config = null,
        Action<ExportProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ExportCoreAsync(input, outputPath, config, onProgress, cancellationToken);
        }
        catch
        {
            DeleteIncompleteOutput(outputPath);
            throw;
        }
    }

    /// <summary>删除失败/取消留下的半成品输出。删不掉只记日志，不能因此覆盖原始失败原因。</summary>
    private void DeleteIncompleteOutput(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
                _logger.LogInformation("[ExportCleanup] 已删除未完成的输出文件: {Output}", outputPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ExportCleanup] 删除半成品失败（文件可能被占用）: {Output}", outputPath);
        }
    }

    private async Task ExportCoreAsync(
        ExportInput input,
        string outputPath,
        ExportConfig? config,
        Action<ExportProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        config ??= new ExportConfig();

        // BGM 模式（issue #22）：渲染前校验各分镜的 vocals 切片源还在（预检后可能被外部删掉）。
        // §红线「兜底只兜过程不兜结果」：缺了绝不回退原混音轨（那等于给用户一条 BGM 没去掉的片子），
        // 直接让该条成片失败并给出可自救的人话。
        if (config.BgmPath is not null)
        {
            foreach (var seg in input.Segments)
            {
                if (seg.Audio is { } ov && !File.Exists(ov.Path))
                {
                    throw new ExportException(
                        $"人声分离产物缺失（{Path.GetFileName(seg.Path)}），无法去除原 BGM。" +
                        "请重新导出（会自动重新分离）");
                }
            }
        }

        var resolution = ResolveResolution(config.Resolution, input.MaxWidth, input.MaxHeight);

        onProgress?.Invoke(new ExportProgress(
            ExportPhase.Encoding, 0.05, $"正在编码拼接 {input.Segments.Count} 个片段..."));

        Action<FFmpegProgress> reportEncode = ffmpegProgress => onProgress?.Invoke(new ExportProgress(
            ExportPhase.Encoding,
            0.05 + ffmpegProgress.Percentage * 0.9,
            $"正在编码... {(int)(ffmpegProgress.Percentage * 100)}%"));

        var isHardware = config.Codec.IsHardware();

        // 硬件解码仅对「大分辨率源」（4K 等，长边 > 1920）+「NVIDIA CUDA」启用。
        // 为什么只 cuda：导出滤镜图是软件滤镜（trim/scale/pad/concat），需要帧在系统内存。
        //   · cuda：-hwaccel cuda 会在软件滤镜前自动 hwdownload 回内存，可用（N 卡用户即本次报障硬件）；
        //   · qsv：朴素 -hwaccel qsv 喂软件滤镜会 -40「Function not implemented」（构建机 Intel 实测失败），
        //          正确做法要全程 vpp_qsv GPU 滤镜链，复杂且本机才有，故 qsv/AMD/无卡一律不启用 → 走原软解路径（零回归）。
        // 真凶是 4K 源软件解码 + 多路并发抢 CPU；cuda 解码可大幅缓解。cuda 路径构建机测不了，
        // 靠下方软解兜底保正确性，提速效果需 N 卡机器实测（CLAUDE.md §C / §兼容性总纲）。
        var sourceMaxDim = Math.Max(input.MaxWidth, input.MaxHeight);
        var decodeHw = (sourceMaxDim > 1920 && Infrastructure.HardwareEncoderProbe.DecodeHwaccel == "cuda")
            ? "cuda" : null;
        if (decodeHw is not null)
        {
            _logger.LogInformation("[ExportDecode] 源长边={Dim} 启用 CUDA 硬件解码", sourceMaxDim);
        }

        try
        {
            await _ffmpeg.ConcatAsync(
                input.Segments, outputPath, resolution,
                config.Quality.Crf(config.Codec), config.Codec.FfmpegCodec(),
                isHardware: isHardware,
                videoBitrateKbps: config.Quality.VideoBitrateKbps(config.Codec),
                onProgress: reportEncode, cancellationToken: cancellationToken,
                decodeHwaccel: decodeHw);
        }
        catch (OperationCanceledException)
        {
            throw; // 用户主动取消，不兜底
        }
        catch (VideoProcessing.FFmpegException ffEx)
            when (VideoProcessing.FFmpegException.Classify(ffEx) == VideoProcessing.FFmpegFailureClass.Oom)
        {
            // 内存不足（exit -12 / "Cannot allocate memory"）—— 发生在 filter graph 阶段，
            // 与编码器无关，换 libx264 无济于事（滤镜图相同，OOM 依旧）。
            // 正确做法：降低输出分辨率（≤1080p，像素降 ~4× → filter 内存降 ~4×）后用原编码器重跑。
            var retryRes = ClampResolutionToMax1080(resolution);
            _logger.LogWarning(ffEx,
                "[ExportOOM] 内存不足，降分辨率重试 {From} -> {To}: {Output}",
                resolution, retryRes, outputPath);
            if (retryRes == resolution)
            {
                // 已是 ≤1080p 仍 OOM —— 本机内存确实不够，交上层串行/人话处理。
                throw;
            }
            onProgress?.Invoke(new ExportProgress(
                ExportPhase.Encoding, 0.05, "内存不足，正在降到 1080p 重新编码..."));
            await _ffmpeg.ConcatAsync(
                input.Segments, outputPath, retryRes,
                config.Quality.Crf(config.Codec), config.Codec.FfmpegCodec(),
                isHardware: isHardware,
                videoBitrateKbps: config.Quality.VideoBitrateKbps(config.Codec),
                onProgress: reportEncode, cancellationToken: cancellationToken,
                decodeHwaccel: decodeHw);
            _logger.LogInformation("[ExportOOM] 降 1080p 重试成功: {Output}", outputPath);
        }
        catch (VideoProcessing.FFmpegException ffEx)
            when (isHardware
                 && VideoProcessing.FFmpegException.Classify(ffEx) == VideoProcessing.FFmpegFailureClass.EncoderCrash)
        {
            // 硬件编码器（NVENC/QSV/AMF）崩溃 (0xC0000005) 或产出 0 帧
            // (exit -542398533 "received no packets") —— filter graph 没问题，是编码器本身的事。
            // 自动降级到 CPU libx264 重试一次：慢但一定能导出，守住「装上即跑」底线。
            // 构建机无对应 GPU，这类坑测不出（见 CLAUDE.md）。
            _logger.LogWarning(ffEx,
                "[ExportFallback] 硬件编码失败，降级 CPU libx264 重试: {Output}", outputPath);
            onProgress?.Invoke(new ExportProgress(
                ExportPhase.Encoding, 0.05, "硬件编码失败，正在用 CPU 重新编码（较慢）..."));
            await _ffmpeg.ConcatAsync(
                input.Segments, outputPath, resolution,
                config.Quality.Crf(ExportCodec.H264), ExportCodec.H264.FfmpegCodec(),
                isHardware: false,
                videoBitrateKbps: config.Quality.VideoBitrateKbps(ExportCodec.H264),
                onProgress: reportEncode, cancellationToken: cancellationToken,
                decodeHwaccel: null);
            _logger.LogInformation("[ExportFallback] CPU 降级编码成功: {Output}", outputPath);
        }
        catch (VideoProcessing.FFmpegException ffEx)
            when (decodeHw != null
                 && VideoProcessing.FFmpegException.Classify(ffEx) != VideoProcessing.FFmpegFailureClass.InvalidAudioData)
        {
            // 硬件解码超时/失败（卡死、HW frames 初始化失败、某些 4K HEVC 不被 GPU 接受等）
            // → 关掉硬解、纯软解重试一次（编码器保持不变）。守住「装上即跑」：硬解只为提速，失败绝不让用户导不出。
            // 构建机是 Intel，测不了 N 卡 cuda 解码路径，全靠这条兜底保正确性（CLAUDE.md §C / §兼容性总纲）。
            // 注：Oom / 硬件编码崩溃已在上面分支处理；走到这里的是超时或其它硬解相关失败。
            _logger.LogWarning(ffEx,
                "[ExportDecodeFallback] 硬件解码失败/超时，关硬解软解重试: {Output}", outputPath);
            onProgress?.Invoke(new ExportProgress(
                ExportPhase.Encoding, 0.05, "硬件解码不可用，正在用软件解码重新导出（较慢）..."));
            await _ffmpeg.ConcatAsync(
                input.Segments, outputPath, resolution,
                config.Quality.Crf(config.Codec), config.Codec.FfmpegCodec(),
                isHardware: isHardware,
                videoBitrateKbps: config.Quality.VideoBitrateKbps(config.Codec),
                onProgress: reportEncode, cancellationToken: cancellationToken,
                decodeHwaccel: null);
            _logger.LogInformation("[ExportDecodeFallback] 软解重试成功: {Output}", outputPath);
        }
        // Timeout / Other（且无硬解可关）：不盲目重试，原样抛出，由上层 ExportErrorMessage 翻译成人话。

        // BGM 模式（issue #22）：拼接完成后独立一步给成片铺所选 BGM（视频流 -c copy 直拷，只改音频）。
        // 失败会向上抛 → ExportAsync 外层删掉半成品，绝不留一条「BGM 没换上」的成片给用户。
        if (config.BgmPath is { } bgmPath)
        {
            onProgress?.Invoke(new ExportProgress(ExportPhase.Encoding, 0.97, "正在铺背景音乐…"));
            await Bgm.BgmApplier.ApplyAsync(_ffmpeg, outputPath, bgmPath, config.BgmVolume, cancellationToken);
            _logger.LogInformation("[BgmDiag] 成片已铺 BGM: {Bgm} vol={Vol:F2}",
                Path.GetFileName(bgmPath), config.BgmVolume);
        }

        onProgress?.Invoke(new ExportProgress(ExportPhase.Completed, 1.0, "导出完成"));
        _logger.LogInformation("导出完成: {Output}", outputPath);
    }

    /// <summary>把导出分辨率枚举解析成 ffmpeg scale 用的 "W:H" 字符串。供 ExportView 复用以算并发。</summary>
    public static string ResolveResolution(ExportResolution res, int maxWidth, int maxHeight)
    {
        var isLandscape = maxWidth > maxHeight;
        return res switch
        {
            ExportResolution.Original =>
                $"{(maxWidth > 0 ? (maxWidth + 1) / 2 * 2 : 1080)}:" +
                $"{(maxHeight > 0 ? (maxHeight + 1) / 2 * 2 : 1920)}",
            ExportResolution.P1080 => isLandscape ? "1920:1080" : "1080:1920",
            ExportResolution.P720 => isLandscape ? "1280:720" : "720:1280",
            ExportResolution.P480 => isLandscape ? "854:480" : "480:854",
            _ => "1080:1920",
        };
    }

    /// <summary>把 "W:H" 分辨率夹到长边 ≤1920（≤1080p）。已小于则原样返回（== 视为无需重试）。</summary>
    public static string ClampResolutionToMax1080(string resolution)
    {
        var parts = resolution.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var w) || w <= 0
            || !int.TryParse(parts[1], out var h) || h <= 0)
        {
            return "1080:1920";
        }
        var longEdge = Math.Max(w, h);
        if (longEdge <= 1920)
        {
            return resolution; // 已 ≤1080p
        }
        var ratio = 1920.0 / longEdge;
        int Even(double v) => Math.Max(2, (int)Math.Round(v / 2.0) * 2);
        return $"{Even(w * ratio)}:{Even(h * ratio)}";
    }
}
