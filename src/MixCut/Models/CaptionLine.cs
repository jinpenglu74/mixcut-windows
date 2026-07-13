using System.Collections.Generic;
using System.Linq;

namespace MixCut.Models;

/// <summary>
/// 一个带时间的字（供逐句字幕编辑器「按时间重分组」用）。对应 macOS TimedChar。
/// 只存 End（该字结束时间，相对分镜起点秒）；某字的「开始」= 上一字的 End（首字 = 句 Start）。
/// </summary>
public sealed record TimedChar(string Ch, double End);

/// <summary>
/// 一行逐句字幕：文本（原始含标点，显示/烧录时另去标点）+ 相对分镜起点的秒时间窗。
/// <see cref="Chars"/> 存每字时间，供编辑器把分界当「时间」拖动、字随时间在相邻句间迁移/合并。
/// 导出只用 Text/Start/End；Chars 仅编辑器用。对应 macOS CaptionLine。
/// </summary>
public sealed class CaptionLine
{
    public string Text { get; set; } = string.Empty;
    public double Start { get; set; }
    public double End { get; set; }
    public List<TimedChar> Chars { get; set; } = new();

    public CaptionLine() { }

    public CaptionLine(string text, double start, double end, List<TimedChar>? chars = null)
    {
        Text = text;
        Start = start;
        End = end;
        Chars = chars ?? new List<TimedChar>();
    }

    /// <summary>深拷贝——算法按 Swift 值语义处理，改动前须克隆避免别名污染。</summary>
    public CaptionLine Clone() => new(Text, Start, End, new List<TimedChar>(Chars));

    // 值相等（联动分界的 noop 判定 / 单测 == 依赖）。TimedChar 是 record，序列逐项相等即可。
    public override bool Equals(object? obj) =>
        obj is CaptionLine l && Text == l.Text && Start == l.Start && End == l.End && Chars.SequenceEqual(l.Chars);

    public override int GetHashCode() => System.HashCode.Combine(Text, Start, End, Chars.Count);
}
