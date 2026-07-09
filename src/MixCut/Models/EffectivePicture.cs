namespace MixCut.Models;

/// <summary>
/// 分镜「当前生效画面」的单一真源（纯值对象）。播放 / 缩略图 / 导出都从这里取画面，
/// 不再各自直接读 <c>Video.LocalPath</c>——这样「原画面 ↔ AI 替换画面」的择一逻辑只有一处，
/// 杜绝「预览是替换画面、导出却是原画面」的自相矛盾（§兼容性总纲推论 3 同源一致）。
/// 对应 macOS 版 Segment.EffectivePicture。
/// </summary>
public readonly record struct EffectivePicture(
    string VideoPath,
    double StartTime,
    double EndTime,
    int StartFrame,
    int EndFrame,
    double Fps,
    string? ThumbnailPath,
    bool IsReplaced);
