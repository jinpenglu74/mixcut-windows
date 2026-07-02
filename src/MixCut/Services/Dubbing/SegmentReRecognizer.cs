using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MixCut.Services.AI;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.Dubbing;

/// <summary>
/// 分镜台词「精识别」：抽单个分镜区间音频 → 阿里 Paraformer 流式 ASR → 返回文本。
///
/// <para>对齐 mac「阿里 ASR 混合架构」(commit f5f5b39)：whisper 整片转写只用于**切分镜的时间戳**，
/// 每个分镜的**显示台词**由本服务逐段各自重识别一遍 —— 短音频单独识别天生准、带标点，
/// 不受整片时间戳漂移影响。导入流程（自动逐分镜）与分镜库 ↻ 按钮共用这一条路径，
/// 避免 <c>-f s16le</c> 这类易错的裸 PCM ffmpeg 细节在两处各写一份、日后漂移。</para>
/// </summary>
public sealed class SegmentReRecognizer
{
    private readonly ParaformerAsrClient _paraformer;
    private readonly FFmpegRunner _ffmpeg;
    private readonly AppSettings _settings;
    private readonly ILogger<SegmentReRecognizer> _logger;

    public SegmentReRecognizer(
        ParaformerAsrClient paraformer,
        FFmpegRunner ffmpeg,
        AppSettings settings,
        ILogger<SegmentReRecognizer> logger)
    {
        _paraformer = paraformer;
        _ffmpeg = ffmpeg;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// 阿里 ASR 是否可用：配了千问 key 才能精识别；没配就只能退回 whisper 文本。
    /// 导入流程据此优雅跳过（不报错），保证无 key 用户导入照样能完成。
    /// </summary>
    public bool IsAvailable => _settings.HasApiKey(AIProviderType.Qwen);

    /// <summary>
    /// 抽 [startTime, endTime) 区间音频跑 Paraformer，返回识别文本（已 Trim）。
    /// 失败抛异常，由调用方决定保留原文（导入批量）还是翻译成人话 toast（↻ 单段）。
    /// </summary>
    public async Task<string> RecognizeAsync(
        string videoPath, double startTime, double endTime, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            throw new DubException("源视频文件不存在，无法提取音频");

        // 抽为 16k/mono/s16le 裸 PCM。
        var pcmPath = Path.Combine(Path.GetTempPath(), $"mixcut-reasr-{Guid.NewGuid():N}.pcm");
        try
        {
            // -f s16le 必填：输出是裸 PCM（.pcm 非容器格式），不给 ffmpeg 会按扩展名猜错 muxer
            // → 直接 "Conversion failed!"。Paraformer 流式按 3200 字节/100ms(16k·mono·s16le) 切帧，正需裸 s16le 无头。
            var pcmArgs = new[]
            {
                "-ss", $"{Math.Max(0, startTime):F6}", "-i", videoPath,
                "-t", $"{Math.Max(0, endTime - startTime):F6}",
                "-vn", "-acodec", "pcm_s16le", "-ar", "16000", "-ac", "1",
                "-f", "s16le", "-y", pcmPath,
            };
            await _ffmpeg.RunAsync(pcmArgs, cancellationToken: ct);

            if (!File.Exists(pcmPath) || new FileInfo(pcmPath).Length == 0)
                throw new DubException("音频提取失败（该分镜可能无声）");

            var pcm = File.ReadAllBytes(pcmPath);
            _logger.LogInformation("[ParaformerDiag] 分镜区间 [{S:F2},{E:F2}) PCM {Bytes} 字节（{Sec:F1}s）",
                startTime, endTime, pcm.Length, pcm.Length / 32000.0);

            return (await _paraformer.RecognizeAsync(pcm, ct) ?? string.Empty).Trim();
        }
        finally
        {
            try { File.Delete(pcmPath); } catch { }
        }
    }
}
