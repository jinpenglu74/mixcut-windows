using System;
using System.Collections.Generic;
using System.Linq;
using MixCut.Models;

namespace MixCut.Services.Captions;

/// <summary>对齐器需要的最小 word 结构（app 层用 AsrWord 映射进来）。对应 macOS AlignWord。</summary>
public readonly record struct AlignWord(string Text, double Start, double End);

/// <summary>
/// 把「改写台词」按标点切句，用配音 ASR（或比例）给每句配上 [start,end]。纯函数、可单测。
/// 逐行对齐 macOS <c>SentenceTimingAligner</c>：主策略=按去标点字符数比例分到「语音跨度」；
/// 有 words 时用字符顺序匹配精修边界；数字折叠；失败句锚点插值；归一化；逐字时间。
/// </summary>
public static class SentenceTimingAligner
{
    private static readonly HashSet<char> SentenceEnders = new() { '。', '！', '？', '!', '?', '.', ';', '；', '\n' };
    private static readonly HashSet<char> Stripped = new()
    { '，', ',', '、', '。', '！', '？', '!', '?', '.', ';', '；', ' ', '\n', '\r', '\t', '…', '·', '：', ':' };

    /// <summary>去标点/空白，返回纯字符（用于比例与匹配）。</summary>
    public static List<char> Chars(string s) => s.Where(c => !Stripped.Contains(c)).ToList();

    /// <summary>按句末标点/换行切句，返回每句原文（保留内部非句末标点；空/纯标点段丢弃）。</summary>
    public static List<string> SplitSentences(string text)
    {
        var outList = new List<string>();
        var cur = "";
        foreach (var ch in text)
        {
            cur += ch;
            if (SentenceEnders.Contains(ch))
            {
                if (Chars(cur).Count > 0) outList.Add(cur.Trim());
                cur = "";
            }
        }
        if (Chars(cur).Count > 0) outList.Add(cur.Trim());
        return outList;
    }

    public static List<CaptionLine> Align(string text, IReadOnlyList<AlignWord> words, double audioDuration, double segmentDuration)
    {
        var sentences = SplitSentences(text);
        if (sentences.Count == 0) return new List<CaptionLine>();

        // 语音跨度：有 words 用 [首词.start, 末词.end]，否则 [0, audioDuration]
        var spanStart = words.Count > 0 ? words[0].Start : 0;
        var spanEnd = Math.Max(spanStart + 0.01, words.Count > 0 ? words[^1].End : Math.Max(audioDuration, 0.01));

        // 主策略：按去标点字符数比例分配到跨度
        var counts = sentences.Select(s => Math.Max(1, Chars(s).Count)).ToList();
        var total = counts.Sum();
        var lines = new List<CaptionLine>();
        var acc = 0;
        for (var i = 0; i < sentences.Count; i++)
        {
            var a = spanStart + (spanEnd - spanStart) * acc / total;
            acc += counts[i];
            var b = spanStart + (spanEnd - spanStart) * acc / total;
            lines.Add(new CaptionLine(sentences[i], a, b));
        }

        // 精修（有 words 时）
        if (words.Count > 0)
        {
            lines = Refine(lines, sentences, words);
        }

        // 归一化后给每句填「每字时间」（句内按字数均匀，句边界由 ASR/插值决定）
        return Normalize(lines, segmentDuration).Select(WithCharTimes).ToList();
    }

    /// <summary>给一行填充「每字时间」（句内按字数均匀分布在 [start,end]）。</summary>
    private static CaptionLine WithCharTimes(CaptionLine line)
    {
        var l = line.Clone();
        var cs = Chars(line.Text);
        if (cs.Count == 0) { l.Chars = new List<TimedChar>(); return l; }
        var span = Math.Max(0.001, line.End - line.Start);
        l.Chars = cs.Select((c, k) => new TimedChar(c.ToString(), line.Start + span * (k + 1) / cs.Count)).ToList();
        return l;
    }

    /// <summary>阿拉伯数字 → 中文单字（逐位），让「69」与 ASR「六十九」逐字匹配（跳过多余的「十」）。</summary>
    private static readonly Dictionary<char, char> DigitCn = new()
    {
        ['0'] = '〇', ['1'] = '一', ['2'] = '二', ['3'] = '三', ['4'] = '四',
        ['5'] = '五', ['6'] = '六', ['7'] = '七', ['8'] = '八', ['9'] = '九',
    };
    private static char Fold(char c) => DigitCn.TryGetValue(c, out var v) ? v : c;

    /// <summary>字符级时间序列（一个多字 token 的 [start,end] 按字符线性内插；数字折叠成中文单字）。</summary>
    private static List<(char Ch, double Start, double End)> CharTimeline(IReadOnlyList<AlignWord> words)
    {
        var seq = new List<(char, double, double)>();
        foreach (var wd in words)
        {
            var cs = Chars(wd.Text);
            if (cs.Count == 0) continue;
            var dur = Math.Max(0, wd.End - wd.Start) / cs.Count;
            for (var k = 0; k < cs.Count; k++)
            {
                seq.Add((Fold(cs[k]), wd.Start + dur * k, wd.Start + dur * (k + 1)));
            }
        }
        return seq;
    }

    /// <summary>用配音 ASR 精修每句边界（贪心顺序匹配；失败连续句在相邻锚点间按字数插值，杜绝零宽度）。</summary>
    private static List<CaptionLine> Refine(List<CaptionLine> lines, List<string> sentences, IReadOnlyList<AlignWord> words)
    {
        var tl = CharTimeline(words);
        if (tl.Count == 0) return lines;
        var n = sentences.Count;
        var mStart = new double?[n];
        var mEnd = new double?[n];
        var ptr = 0;
        for (var i = 0; i < n; i++)
        {
            var sc = Chars(sentences[i]).Select(Fold).ToList();
            if (sc.Count == 0) continue;
            double? startT = null, endT = null;
            var j = ptr;
            var matched = 0;
            while (j < tl.Count && matched < sc.Count)
            {
                if (tl[j].Ch == sc[matched])
                {
                    if (matched == 0) startT = tl[j].Start;
                    endT = tl[j].End;
                    matched++;
                }
                j++;
            }
            if (startT is { } s && endT is { } e && matched >= Math.Max(1, sc.Count / 2))
            {
                mStart[i] = s;
                mEnd[i] = Math.Max(s + 0.05, e);
                ptr = j;
            }
        }

        // 语音跨度端点（给首尾未匹配句兜底）
        var spanStart = words.Count > 0 ? words[0].Start : (lines.Count > 0 ? lines[0].Start : 0);
        var spanEnd = Math.Max(spanStart + 0.01, words.Count > 0 ? words[^1].End : (lines.Count > 0 ? lines[^1].End : spanStart + 0.01));

        var outLines = lines.Select(l => l.Clone()).ToList();
        var idx = 0;
        while (idx < n)
        {
            if (mStart[idx] is { } ms && mEnd[idx] is { } me)
            {
                outLines[idx].Start = ms; outLines[idx].End = me; idx++; continue;
            }
            // 收集一段连续未匹配 [idx, k)，在前后锚点之间按字数插值
            var k = idx;
            while (k < n && mStart[k] == null) k++;
            var leftAnchor = idx > 0 ? (mEnd[idx - 1] ?? spanStart) : spanStart;
            var rightAnchor = k < n ? (mStart[k] ?? spanEnd) : spanEnd;
            var hi = Math.Max(leftAnchor + 0.0001, rightAnchor);
            var width = hi - leftAnchor;
            var counts = Enumerable.Range(idx, k - idx).Select(s => Math.Max(1, Chars(sentences[s]).Count)).ToList();
            var totalC = counts.Sum();
            var accC = 0;
            for (var offset = 0; offset < k - idx; offset++)
            {
                var s = idx + offset;
                var a = leftAnchor + width * accC / totalC;
                accC += counts[offset];
                var b = leftAnchor + width * accC / totalC;
                outLines[s].Start = a; outLines[s].End = b;
            }
            idx = k;
        }
        return outLines;
    }

    private static List<CaptionLine> Normalize(List<CaptionLine> lines, double segmentDuration)
    {
        var outLines = lines.Select(l => l.Clone()).OrderBy(l => l.Start).ToList();
        for (var i = 0; i < outLines.Count; i++)
        {
            outLines[i].Start = Math.Min(Math.Max(0, outLines[i].Start), segmentDuration);
            outLines[i].End = Math.Min(Math.Max(outLines[i].Start + 0.05, outLines[i].End), segmentDuration);
            if (i > 0 && outLines[i].Start < outLines[i - 1].End) outLines[i].Start = outLines[i - 1].End;
            if (outLines[i].End < outLines[i].Start) outLines[i].End = outLines[i].Start + 0.05;
        }
        return outLines;
    }
}
