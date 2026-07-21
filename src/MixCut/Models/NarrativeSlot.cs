using System.Text.Json;
using System.Text.Json.Serialization;

namespace MixCut.Models;

/// <summary>
/// 自定义叙事结构的一个「段位」：有序号 + 一组系统语义标签（候选取并集）。
/// 对齐 issue #6 / macOS NarrativeSlot。一段最终在成片里放一条分镜，多标签是候选池。
///
/// 序列化成中文标签 JSON（复用 <see cref="SemanticTypeExtensions.ToLabel"/>），随
/// <c>MixStrategy.NarrativeSlotsJson</c> 存库；解码失败返回空列表（不抛，对齐 JsonColumn 行为）。
/// </summary>
/// <param name="MinDuration">该段候选分镜的最短时长（秒），null=不限。对齐 macOS NarrativeSlot.minDuration。</param>
/// <param name="MaxDuration">该段候选分镜的最长时长（秒），null=不限。对齐 macOS NarrativeSlot.maxDuration。</param>
public sealed record NarrativeSlot(
    int Order, List<SemanticType> Tags, double? MinDuration = null, double? MaxDuration = null)
{
    /// <summary>段位列表 → JSON（标签存中文 label，稳定且人类可读）。</summary>
    public static string Serialize(IReadOnlyList<NarrativeSlot> slots) =>
        JsonSerializer.Serialize(
            slots.Select(s => new SlotDto(
                s.Order, s.Tags.Select(t => t.ToLabel()).ToList(), s.MinDuration, s.MaxDuration)).ToList());

    /// <summary>JSON → 段位列表；null/空/损坏 → 空列表（不抛）。</summary>
    public static List<NarrativeSlot> Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new List<NarrativeSlot>();
        }
        try
        {
            var dtos = JsonSerializer.Deserialize<List<SlotDto>>(json) ?? new List<SlotDto>();
            return dtos
                .Select(d => new NarrativeSlot(
                    d.Order,
                    (d.Tags ?? new List<string>()).Select(SemanticTypeExtensions.FromLabel).ToList(),
                    d.MinDuration, d.MaxDuration))   // 老数据无这两个字段 → null = 不限（向后兼容）
                .ToList();
        }
        catch (JsonException)
        {
            return new List<NarrativeSlot>();
        }
    }

    /// <summary>DTO：标签以中文 label 字符串存储，解耦枚举数值变化。</summary>
    private sealed record SlotDto(
        [property: JsonPropertyName("order")] int Order,
        [property: JsonPropertyName("tags")] List<string> Tags,
        [property: JsonPropertyName("minDuration")] double? MinDuration = null,
        [property: JsonPropertyName("maxDuration")] double? MaxDuration = null);
}
