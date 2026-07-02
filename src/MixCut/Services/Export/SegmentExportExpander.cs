namespace MixCut.Services.Export;

/// <summary>一个已生成音频的配音变体引用（纯值类型）。对应 mac VariantRef。</summary>
public readonly record struct VariantRef(Guid DubId, int TextVariantIndex);

/// <summary>一个选中分镜的导出来源描述（纯值类型，从 Segment 构造）。对应 mac SegmentExportSource。</summary>
public sealed record SegmentExportSource(
    Guid SegmentId, int SequenceNumber, string VideoName, bool IsVoiceLocked,
    IReadOnlyList<VariantRef> Variants);

/// <summary>展开后的单个导出任务（DubId==null 表示原版）。对应 mac SegmentExportPlanItem。</summary>
public sealed record SegmentExportPlanItem(Guid SegmentId, Guid? DubId, string FileName);

/// <summary>
/// 把选中分镜展开成「1 原版 + N 变体」的导出任务列表并命名。对应 mac SegmentExportExpander。
/// 命名：原版 <c>{编号}_{视频名}.mp4</c>；变体 <c>{编号}_{视频名}_{A/B…}.mp4</c>（按 TextVariantIndex 升序）。
/// </summary>
public static class SegmentExportExpander
{
    public static IReadOnlyList<SegmentExportPlanItem> Expand(IReadOnlyList<SegmentExportSource> sources)
    {
        var outItems = new List<SegmentExportPlanItem>();
        foreach (var s in sources)
        {
            outItems.Add(new SegmentExportPlanItem(s.SegmentId, null, $"{s.SequenceNumber}_{s.VideoName}.mp4"));
            if (s.IsVoiceLocked) continue;
            foreach (var v in s.Variants.OrderBy(v => v.TextVariantIndex))
            {
                var letter = Letter(v.TextVariantIndex);
                outItems.Add(new SegmentExportPlanItem(
                    s.SegmentId, v.DubId, $"{s.SequenceNumber}_{s.VideoName}_{letter}.mp4"));
            }
        }
        return outItems;
    }

    /// <summary>textVariantIndex → 字母（0→A）；越界回退 "V{index}"。对齐 mac letter(for:)。</summary>
    public static string Letter(int index) =>
        index is >= 0 and < 26 ? ((char)('A' + index)).ToString() : $"V{index}";
}
