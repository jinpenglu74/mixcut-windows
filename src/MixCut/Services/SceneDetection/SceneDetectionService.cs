using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MixCut.Services.VideoProcessing;

namespace MixCut.Services.SceneDetection;

/// <summary>
/// 视频本地分析服务（场景检测 + 静音检测 + I-frame 提取）。
/// 对应 macOS 版 SceneDetectionService。注册为单例服务。
/// </summary>
public sealed class SceneDetectionService
{
    private static readonly Regex PtsTimeRegex = new(@"pts_time:\s*([-+\d.eE]+)", RegexOptions.Compiled);
    private static readonly Regex SceneScoreRegex = new(@"lavfi\.scene_score=([-+\d.eE]+)", RegexOptions.Compiled);
    private static readonly Regex SilenceStartRegex = new(@"silence_start:\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex SilenceEndRegex = new(@"silence_end:\s*([\d.]+)", RegexOptions.Compiled);

    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<SceneDetectionService> _logger;

    public SceneDetectionService(FFmpegRunner ffmpeg, ILogger<SceneDetectionService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>全片解码类检测（场景/静音）的动态超时：时长×10，下限 4 分钟、上限 30 分钟。
    /// 弱机（4 核轻薄本 + 后台抢占）解码 96s 视频就超 FFmpegRunner 默认 3 分钟，写死必冤杀（2026-07-24 实测）。</summary>
    private static TimeSpan DecodeTimeout(double durationSec) =>
        TimeSpan.FromSeconds(Math.Clamp(durationSec > 0 ? durationSec * 10 : 240, 240, 1800));

    /// <summary>使用 FFmpeg scene filter 检测镜头切换点。scene filter 是 software，不加 hwaccel。</summary>
    public async Task<IReadOnlyList<SceneBoundary>> DetectScenesAsync(
        string videoPath, double threshold = 0.3, CancellationToken cancellationToken = default,
        double durationSec = 0)
    {
        var args = new[]
        {
            "-i", videoPath,
            "-vf", $"select='gt(scene,{threshold.ToString(CultureInfo.InvariantCulture)})'," +
                   "metadata=print:key=lavfi.scene_score",
            "-f", "null", "-",
        };
        var stderr = await _ffmpeg.RunForStderrAsync(args, cancellationToken, DecodeTimeout(durationSec));
        var result = ParseSceneBoundaries(stderr);
        _logger.LogInformation(
            "[SceneDiag] count={Count} nonZero={NonZero} maxScore={MaxScore:F4}",
            result.Count, result.Count(x => x.Confidence > 0),
            result.Count == 0 ? 0 : result.Max(x => x.Confidence));
        return result;
    }

    /// <summary>使用 FFmpeg silencedetect 检测静音/停顿段。</summary>
    public async Task<IReadOnlyList<SilencePeriod>> DetectSilenceAsync(
        string videoPath, string noiseThreshold = "-30dB", double minDuration = 0.3,
        CancellationToken cancellationToken = default, double durationSec = 0)
    {
        var args = new[]
        {
            "-i", videoPath,
            "-af", $"silencedetect=noise={noiseThreshold}:d={minDuration.ToString(CultureInfo.InvariantCulture)}",
            "-f", "null", "-",
        };
        var stderr = await _ffmpeg.RunForStderrAsync(args, cancellationToken, DecodeTimeout(durationSec));
        return ParseSilencePeriods(stderr);
    }

    /// <summary>提取视频中所有 I-frame 的精确时间戳（ffprobe 不解码，速度快）。</summary>
    /// <param name="durationSec">视频时长（秒），用于动态算超时；0 = 未知，给保守值。</param>
    public async Task<IReadOnlyList<double>> ExtractIFramesAsync(
        string videoPath, CancellationToken cancellationToken = default, double durationSec = 0)
    {
        // v0.15.0 改 packet 级探测：旧的 frame 级（-skip_frame nokey + show_frames）要真解码关键帧，
        // 96s 视频在弱机实测 19s（被后台抢占时 75s+）；packet 级只读容器不解码，同视频 0.7s（快 26 倍），
        // 关键包 pts_time 与解码后 I-frame 时间戳逐一相等（2026-07-24 实测对照）。
        var args = new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", videoPath,
        };
        // 超时按时长动态算（CLAUDE.md §10 反模式：写死的超时；原 60s 在弱机会把探测冤杀成 0 项）。
        var timeout = (int)Math.Clamp(durationSec > 0 ? durationSec * 3 : 300, 120, 600);
        var stdout = await _ffmpeg.RunProbeAsync(args, timeoutSeconds: timeout, cancellationToken);
        return ParseIFrameTimes(stdout);
    }

    /// <summary>
    /// 并行执行所有本地视频分析（场景检测 + 静音检测 + I-frame 提取）。
    /// 整体硬超时按时长动态算：即使内部某步挂起也会被取消。
    /// </summary>
    public async Task<VideoLocalAnalysis> AnalyzeLocallyAsync(
        string videoPath, double duration, double fps, double sceneThreshold = 0.3,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // v0.15.0：原 4 分钟写死 —— 弱机上场景检测要全片解码，长视频必撞线。改为时长×10（4min~30min）。
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(duration > 0 ? duration * 10 : 240, 240, 1800)));
        var ct = timeoutCts.Token;

        // 粗粒度进度：三步并行，每完成一步贡献 1/3。
        var done = 0;
        var lockObj = new object();
        void Tick()
        {
            int now;
            lock (lockObj) { now = ++done; }
            progress?.Report(now / 3.0);
        }

        try
        {
            // 三步并行执行（互相独立），每步独立容错。
            var scenesTask = SafeAsync(
                () => DetectScenesAsync(videoPath, sceneThreshold, ct, duration),
                Array.Empty<SceneBoundary>(), "场景检测", Tick);
            var silencesTask = SafeAsync(
                () => DetectSilenceAsync(videoPath, cancellationToken: ct, durationSec: duration),
                Array.Empty<SilencePeriod>(), "静音检测", Tick);
            var iframesTask = SafeAsync(
                () => ExtractIFramesAsync(videoPath, ct, duration),
                Array.Empty<double>(), "I-frame 提取", Tick);

            await Task.WhenAll(scenesTask, silencesTask, iframesTask);

            var iframes = await iframesTask;
            if (iframes.Count == 0 && duration > 1)
            {
                // 任何正常视频至少有 1 个 I-frame；0 项 = 探测超时/输出异常（静音检测 0 项才是正常情况）。
                _logger.LogWarning(
                    "[SceneDiag] I-frame 提取 0 项（时长 {Dur:F0}s 不应为 0）—— 可能探测被冤杀或输出异常，" +
                    "边界切分回退用场景检测", duration);
            }

            return new VideoLocalAnalysis(
                await scenesTask, await silencesTask, iframes, duration, fps);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            throw SceneDetectionException.Timeout();
        }
    }

    private async Task<IReadOnlyList<T>> SafeAsync<T>(
        Func<Task<IReadOnlyList<T>>> work, IReadOnlyList<T> fallback, string label,
        Action? onDone = null)
    {
        try
        {
            var result = await work();
            _logger.LogInformation("{Label}完成: {Count} 项", label, result.Count);
            onDone?.Invoke();
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("{Label}失败: {Message}", label, ex.Message);
            onDone?.Invoke();
            return fallback;
        }
    }

    // ---- 解析方法 ----

    internal static IReadOnlyList<SceneBoundary> ParseSceneBoundaries(string output)
    {
        var boundaries = new List<SceneBoundary>();
        double? pendingTime = null;
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("Parsed_metadata", StringComparison.Ordinal)
                && !line.Contains("lavfi.scene_score", StringComparison.Ordinal))
            {
                continue;
            }

            var timeMatch = PtsTimeRegex.Match(line);
            if (timeMatch.Success
                && double.TryParse(timeMatch.Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var time))
            {
                pendingTime = time;
            }

            var scoreMatch = SceneScoreRegex.Match(line);
            if (scoreMatch.Success
                && pendingTime is { } pts
                && double.TryParse(scoreMatch.Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var score))
            {
                boundaries.Add(new SceneBoundary(pts, score));
                pendingTime = null;
            }
        }
        return boundaries.OrderBy(b => b.Time).ToList();
    }

    private static IReadOnlyList<SilencePeriod> ParseSilencePeriods(string output)
    {
        var periods = new List<SilencePeriod>();
        double? currentStart = null;

        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("silencedetect", StringComparison.Ordinal))
            {
                continue;
            }

            var startMatch = SilenceStartRegex.Match(line);
            if (startMatch.Success
                && double.TryParse(startMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var start))
            {
                currentStart = start;
            }

            var endMatch = SilenceEndRegex.Match(line);
            if (endMatch.Success
                && double.TryParse(endMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var end)
                && currentStart is { } s)
            {
                periods.Add(new SilencePeriod(s, end));
                currentStart = null;
            }
        }
        return periods.OrderBy(p => p.Start).ToList();
    }

    private static IReadOnlyList<double> ParseIFrameTimes(string stdout)
    {
        // packet=pts_time,flags 的 csv 输出形如「0.933333,K__」（关键包）/「1.000000,___」（普通包）。
        // ⚠ 必须按逗号取首字段再 parse：ffprobe 8.x 的 csv writer 会在行尾多打一个逗号
        // （旧 frame 级输出「0.000000,」直接 TryParse 整行必失败 → 全片解析成 0 项，正是
        // 2026-07-24 弱机「I-frame 提取完成: 0 项」的真凶）。
        var times = new List<double>();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var fields = line.Split(',');
            // 有 flags 列时只收关键包；无 flags 列（防御旧格式）全收。
            if (fields.Length >= 2 && fields[1].Length > 0 && !fields[1].Contains('K'))
            {
                continue;
            }
            if (double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            {
                times.Add(t);
            }
        }
        return times;
    }
}
