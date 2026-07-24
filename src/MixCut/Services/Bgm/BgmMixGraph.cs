using System.Globalization;

namespace MixCut.Services.Bgm;

/// <summary>
/// 「整片铺 BGM」这一步的 ffmpeg 滤镜图（issue #22 §3.3-3，对应 mac GlobalBGMMixGraph）。
/// 输入 0 = 纯口播成片（视频流 -c copy 直拷不重编码），输入 1 = 所选 BGM（配 -stream_loop -1 负责短则循环）；
/// atrim=0:D 负责长则截断；afade 只淡 BGM 一路（口播不淡出）；amix normalize=0 保持口播原响度。
/// 抽成纯函数便于单测（截断边界 / 音量夹取 / 短片淡出）。
/// </summary>
public static class BgmMixGraph
{
    /// <summary>
    /// 构建 -filter_complex 字符串。
    /// <paramref name="finalDurationSeconds"/>：成片时长（ffprobe 读取，调用方保证 &gt; 0）；
    /// <paramref name="volume"/>：BGM 音量 0–1（超界夹取）。
    /// 淡出起点 st = max(0, D-1)，时长 min(1, D)（成片短于 1 秒的极端情况从 0 开始淡）。
    /// </summary>
    public static string Build(double finalDurationSeconds, double volume)
    {
        var d = Math.Max(0.0, finalDurationSeconds);
        var v = Math.Clamp(volume, 0.0, 1.0);
        var fadeStart = Math.Max(0.0, d - 1.0);
        var fadeDur = Math.Min(1.0, d);

        string F(double x) => x.ToString("F3", CultureInfo.InvariantCulture);

        return
            $"[1:a]atrim=0:{F(d)},asetpts=PTS-STARTPTS,aresample=44100," +
            $"volume={v.ToString("F2", CultureInfo.InvariantCulture)}," +
            $"afade=t=out:st={F(fadeStart)}:d={F(fadeDur)}[bgm];" +
            "[0:a][bgm]amix=inputs=2:duration=first:normalize=0[aout]";
    }
}
