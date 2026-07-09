using MixCut.Utilities;

namespace MixCut.Services.ShotEdit;

/// <summary>一个切出的物理镜头帧区间（纯值）。OrderIndex 从 1 递增。对应 mac ShotRange。</summary>
public readonly record struct ShotRange(int OrderIndex, int StartFrame, int EndFrame);

/// <summary>
/// 分镜头切分纯算法：在一条逻辑分镜的时间窗口内，按画面切换点贪心切出若干物理镜头。
/// 不变式：所有镜头按序<b>首尾无缝铺满</b> [segmentStart, segmentEnd]、总时长恒等于分镜时长。
/// 纯函数、零外部依赖，便于单测。对应 macOS MixCutCore.ShotSegmentationEngine。
/// </summary>
public static class ShotSegmentationEngine
{
    /// <summary>每个镜头最短时长（秒），避免碎片。对应 mac minShotSeconds。</summary>
    public const double MinShotSeconds = 0.5;

    /// <summary>
    /// 把分镜窗口按场景切点切成物理镜头。<paramref name="sceneCutTimes"/> 是全片场景切换的绝对时间点（秒）。
    /// 贪心接受：某切点距上一个已接受边界 ≥ 0.5s 且距 segmentEnd ≥ 0.5s 才接受（保证每段与末段都 ≥0.5s）。
    /// 切不出内部点 → 返回覆盖整段的单个镜头；fps≤0 或区间非法 → 返回空。
    /// </summary>
    public static IReadOnlyList<ShotRange> Split(
        double segmentStart, double segmentEnd,
        IReadOnlyList<double> sceneCutTimes, double fps,
        double minShotSeconds = MinShotSeconds)
    {
        if (fps <= 0 || segmentEnd <= segmentStart)
        {
            return Array.Empty<ShotRange>();
        }

        // 只取严格落在分镜窗口内部的切点，升序。
        var interior = sceneCutTimes
            .Where(t => t > segmentStart && t < segmentEnd)
            .OrderBy(t => t)
            .ToList();

        var accepted = new List<double>();
        var last = segmentStart;
        foreach (var t in interior)
        {
            if (t - last >= minShotSeconds && segmentEnd - t >= minShotSeconds)
            {
                accepted.Add(t);
                last = t;
            }
        }

        var bounds = new List<double> { segmentStart };
        bounds.AddRange(accepted);
        bounds.Add(segmentEnd);

        var shots = new List<ShotRange>();
        for (var i = 0; i < bounds.Count - 1; i++)
        {
            var sf = FrameTime.SecondsToFrame(bounds[i], fps);
            var ef = FrameTime.SecondsToFrame(bounds[i + 1], fps);
            if (ef > sf)
            {
                shots.Add(new ShotRange(shots.Count + 1, sf, ef));
            }
        }
        return shots;
    }
}
