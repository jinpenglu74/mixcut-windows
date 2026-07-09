namespace MixCut.Services.ShotEdit;

/// <summary>
/// 分镜头可编辑性 / 合成完整性规则（纯逻辑）。对应 macOS MixCutCore.ShotEditRules。
/// </summary>
public static class ShotEditRules
{
    /// <summary>可生成 AI 变体的最短镜头时长（秒）。</summary>
    public const double MinSeconds = 2.0;

    /// <summary>可生成 AI 变体的最长镜头时长（秒）。</summary>
    public const double MaxSeconds = 10.0;

    /// <summary>该镜头时长是否落在可编辑区间 [2s, 10s]（可生成 AI 变体）。</summary>
    public static bool IsEditable(double durationSeconds) =>
        durationSeconds >= MinSeconds && durationSeconds <= MaxSeconds;

    /// <summary>不可编辑时的人话原因（超上限 / 不足下限）；可编辑时返回 null。</summary>
    public static string? IneligibleReason(double durationSeconds)
    {
        if (durationSeconds > MaxSeconds)
        {
            return $"该分镜头 {durationSeconds:F1}s 超过 {MaxSeconds:F0}s 上限，请用帧级调整拆短后再替换";
        }
        if (durationSeconds < MinSeconds)
        {
            return $"该分镜头 {durationSeconds:F1}s 不足 {MinSeconds:F0}s 下限，无法替换";
        }
        return null;
    }

    /// <summary>
    /// 合成完整性：镜头数 &gt; 0 且 1..N 每个位置都有选择。占位选择用 orderIndex → 变体 id（null=原版）表达，
    /// 原版恒可选，故只要每个 orderIndex 都在字典里即成立。
    /// </summary>
    public static bool CanCompose(int shotCount, IReadOnlyDictionary<int, Guid?> selections)
    {
        if (shotCount <= 0) return false;
        for (var i = 1; i <= shotCount; i++)
        {
            if (!selections.ContainsKey(i)) return false;
        }
        return true;
    }
}
