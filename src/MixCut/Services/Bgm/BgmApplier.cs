using System.Globalization;
using System.IO;
using MixCut.Services.Export;
using MixCut.Services.VideoProcessing;

namespace MixCut.Services.Bgm;

/// <summary>
/// 给拼接完成的纯口播成片铺整片 BGM（issue #22 §3.3-3）。
/// -stream_loop -1 负责「短则循环」，滤镜图（<see cref="BgmMixGraph"/>）里 atrim 负责「长则截断」+
/// 结尾 1s 只淡 BGM；视频流 -c copy 直拷不重编码（这一步只改音频，很快）。
/// 失败直接抛给调用方 → 调用方删半成品，绝不留「BGM 没换上」的成片（§红线：兜底只兜过程不兜结果）。
/// </summary>
public static class BgmApplier
{
    public static async Task ApplyAsync(
        FFmpegRunner ffmpeg, string videoPath, string bgmPath, double volume, CancellationToken ct)
    {
        if (!File.Exists(bgmPath))
        {
            throw new ExportException("所选背景音乐文件已不存在，请到「BGM 库」确认后重新选择。");
        }

        // 成片时长：ffprobe format=duration。不能用 ProbeDurationAsync —— 它按路径缓存，
        // 重复导出同名输出文件会命中上一条成片的旧时长。
        var durText = await ffmpeg.RunProbeAsync(
            new[]
            {
                "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", videoPath,
            },
            cancellationToken: ct);
        if (!double.TryParse(durText.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var duration)
            || duration <= 0)
        {
            // 读不到时长 → 报错，不硬导（循环/截断/淡出全都依赖时长）。
            throw new ExportException("无法读取成片时长，未能替换背景音乐，请重试");
        }

        var tmp = videoPath + ".bgm.tmp.mp4";
        try
        {
            var graph = BgmMixGraph.Build(duration, volume);
            await ffmpeg.RunAsync(
                new[]
                {
                    "-y",
                    "-i", videoPath,
                    "-stream_loop", "-1", "-i", bgmPath,
                    "-filter_complex", graph,
                    "-map", "0:v", "-map", "[aout]",
                    "-c:v", "copy",
                    "-c:a", "aac", "-b:a", "192k", "-ar", "44100", "-ac", "2",
                    "-movflags", "+faststart",
                    tmp,
                },
                timeout: TimeSpan.FromSeconds(Math.Max(180, duration * 2)),
                cancellationToken: ct);
            File.Move(tmp, videoPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 临时文件删失败无所谓 */ }
        }
    }
}
