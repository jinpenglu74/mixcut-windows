using System;
using System.Collections.Generic;
using System.Linq;
using MixCut.Models;

namespace MixCut.Services.Captions;

/// <summary>
/// 逐句字幕「联动分界」编辑（纯函数、可单测）。逐行对齐 macOS <c>CaptionBoundaryEditor</c>。
/// 分界 = 相邻两句之间的时间点；把它当时间拖动，字按各自时间在两句间迁移；
/// 右句被吃满 → 合并删除；绝不把左句掏空。
/// </summary>
public static class CaptionBoundaryEditor
{
    /// <summary>取一句的逐字时间；无 Chars（旧数据）时按整句 Text 在 [start,end] 均匀合成一份。</summary>
    public static List<TimedChar> CharsOf(CaptionLine line)
    {
        if (line.Chars.Count > 0) return new List<TimedChar>(line.Chars);
        var cs = line.Text.ToCharArray();
        if (cs.Length == 0) return new List<TimedChar>();
        var span = Math.Max(0.001, line.End - line.Start);
        return cs.Select((c, k) => new TimedChar(c.ToString(), line.Start + span * (k + 1) / cs.Length)).ToList();
    }

    /// <summary>
    /// 把第 i 句在「逐字时间中点」拆成两句（Windows 增强：用户手动分句）。字总量守恒、顺序不变，
    /// 拆点 = 前半末字的结束时间。单字句（&lt;2 字）不可拆，原样返回。可反复拆得任意粒度。
    /// </summary>
    public static List<CaptionLine> SplitLine(List<CaptionLine> lines, int i)
    {
        if (i < 0 || i >= lines.Count) return lines;
        var chars = CharsOf(lines[i]);
        if (chars.Count < 2) return lines;          // 单字不可拆
        var mid = chars.Count / 2;
        var leftChars = chars.Take(mid).ToList();
        var rightChars = chars.Skip(mid).ToList();
        var boundary = leftChars[^1].End;
        var outLines = lines.Select(l => l.Clone()).ToList();
        var orig = outLines[i];
        outLines[i] = new CaptionLine(string.Concat(leftChars.Select(c => c.Ch)), orig.Start, boundary, leftChars);
        outLines.Insert(i + 1, new CaptionLine(string.Concat(rightChars.Select(c => c.Ch)), boundary, orig.End, rightChars));
        return outLines;
    }

    /// <summary>移动第 i 句与第 i+1 句之间的分界到时间 t，返回新分区（字总量不变、顺序不变）。</summary>
    public static List<CaptionLine> MoveBoundary(List<CaptionLine> lines, int afterIndex, double t, double minGap = 0.05)
    {
        var i = afterIndex;
        if (i < 0 || i + 1 >= lines.Count) return lines;
        var outLines = lines.Select(l => l.Clone()).ToList();
        // 分界最左不越过左句起点+gap（保左句非空），最右可推到右句止（吃满→合并）
        var b = Math.Min(Math.Max(t, outLines[i].Start + minGap), outLines[i + 1].End);
        var merged = CharsOf(outLines[i]).Concat(CharsOf(outLines[i + 1])).ToList();
        if (merged.Count == 0) return lines;
        var left = merged.Where(c => c.End <= b + 0.0005).ToList();
        var right = merged.Where(c => c.End > b + 0.0005).ToList();
        if (right.Count == 0)
        {
            // 右句被吃满 → 合并删除，字全并入左句
            outLines[i].Chars = merged;
            outLines[i].Text = string.Concat(merged.Select(c => c.Ch));
            outLines[i].End = outLines[i + 1].End;
            outLines.RemoveAt(i + 1);
        }
        else if (left.Count == 0)
        {
            return lines;                                  // 不允许掏空左句
        }
        else
        {
            outLines[i].Chars = left;
            outLines[i].Text = string.Concat(left.Select(c => c.Ch));
            outLines[i].End = b;
            outLines[i + 1].Chars = right;
            outLines[i + 1].Text = string.Concat(right.Select(c => c.Ch));
            outLines[i + 1].Start = b;
        }
        return outLines;
    }
}
