using Microsoft.Extensions.Logging;
using MixCut.Services.SceneDetection;

namespace MixCut.Services.ShotEdit;

/// <summary>
/// 分镜头切分服务：对源视频做画面变化度检测（阈值 0.3），把结果喂给纯算法
/// <see cref="ShotSegmentationEngine"/> 切成物理镜头帧区间。不落库（落库在 ViewModel 层）。
/// 对应 macOS ShotSlicerService。注册为单例服务。
/// </summary>
public sealed class ShotSlicerService
{
    private readonly SceneDetectionService _sceneDetection;
    private readonly ILogger<ShotSlicerService> _logger;

    public ShotSlicerService(SceneDetectionService sceneDetection, ILogger<ShotSlicerService> logger)
    {
        _sceneDetection = sceneDetection;
        _logger = logger;
    }

    /// <summary>
    /// 计算某分镜窗口内的物理镜头帧区间。切不出内部切点 → 单个覆盖整段的镜头。
    /// </summary>
    public async Task<IReadOnlyList<ShotRange>> ComputeShotsAsync(
        string videoPath, double segmentStart, double segmentEnd, double fps,
        double sceneThreshold = 0.3, CancellationToken ct = default)
    {
        var boundaries = await _sceneDetection.DetectScenesAsync(videoPath, sceneThreshold, ct);
        var cutTimes = boundaries.Select(b => b.Time).ToList();
        var shots = ShotSegmentationEngine.Split(segmentStart, segmentEnd, cutTimes, fps);
        _logger.LogInformation(
            "[ShotSliceDiag] video={Video} window=[{Start:F2},{End:F2}] fps={Fps:F2} cuts={Cuts} shots={Shots}",
            System.IO.Path.GetFileName(videoPath), segmentStart, segmentEnd, fps, cutTimes.Count, shots.Count);
        return shots;
    }
}
