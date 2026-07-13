using System;
using System.Linq;
using MixCut.Services.Captions;
using Xunit;

namespace MixCut.Tests.Services.Captions;

/// <summary>逐句字幕自动对齐器测试（移植 macOS SentenceTimingAlignerTests，同期望值）。</summary>
public class SentenceTimingAlignerTests
{
    private static AlignWord W(string t, double s, double e) => new(t, s, e);

    [Fact]
    public void ProportionalNoWords()
    {
        // 你好啊(3字) / 今天天气不错(6字) → 3:6，跨度[0,10]
        var lines = SentenceTimingAligner.Align("你好啊。今天天气不错！", Array.Empty<AlignWord>(), 10, 12);
        Assert.Equal(2, lines.Count);
        Assert.Contains("你好", lines[0].Text);
        Assert.True(Math.Abs(lines[0].Start - 0) < 0.01);
        Assert.True(Math.Abs(lines[0].End - 10.0 * 3.0 / 9.0) < 0.01); // ≈3.333
        Assert.True(Math.Abs(lines[1].End - 10.0) < 0.01);
        Assert.True(lines[0].End <= lines[1].Start + 0.001);
    }

    [Fact]
    public void ProportionalWithSpan()
    {
        var words = new[] { W("你好啊哈", 1.0, 5.0), W("今天不错", 5.0, 9.0) };
        var lines = SentenceTimingAligner.Align("你好啊哈。今天不错。", words, 10, 12);
        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].Start is >= 0.99 and <= 1.01);
        Assert.True(lines[1].End <= 9.01);
    }

    [Fact]
    public void Clamp()
    {
        var lines = SentenceTimingAligner.Align("唯一一句话。", new[] { W("唯一一句话", 0, 100) }, 100, 8);
        Assert.Single(lines);
        Assert.True(lines[0].Start >= 0);
        Assert.True(lines[0].End <= 8.0 + 0.001);
    }

    [Fact]
    public void EmptyText()
    {
        Assert.Empty(SentenceTimingAligner.Align("  。！", Array.Empty<AlignWord>(), 5, 5));
    }

    [Fact]
    public void CharsPopulated()
    {
        var lines = SentenceTimingAligner.Align("你好啊。今天天气不错！", Array.Empty<AlignWord>(), 10, 12);
        Assert.Equal(3, lines[0].Chars.Count);
        Assert.Equal(6, lines[1].Chars.Count);
        Assert.Equal("你好啊", string.Concat(lines[0].Chars.Select(c => c.Ch)));
        Assert.True(lines[0].Chars[^1].End <= lines[0].End + 0.001);
        Assert.True(lines[1].Chars[0].End < lines[1].Chars[5].End);
    }

    [Fact]
    public void FailedMiddleInterpolated()
    {
        // 只有首尾能与 ASR 匹配，中间两句必须拿到可见时长（不塌零宽度）
        var words = new[] { W("你好", 0, 2), W("再见", 8, 10) };
        var lines = SentenceTimingAligner.Align(
            "你好。中间第一句话。中间第二句更长一些。再见。", words, 10, 11);
        Assert.Equal(4, lines.Count);
        foreach (var l in lines) Assert.True(l.End - l.Start > 0.1);
        for (var k = 1; k < lines.Count; k++) Assert.True(lines[k].Start >= lines[k - 1].End - 0.001);
        Assert.True((lines[2].End - lines[2].Start) > (lines[1].End - lines[1].Start));
    }

    [Fact]
    public void DigitFoldingMatches()
    {
        // 台词「69」，ASR 念「六十九」→ 折叠后「六九」跳过「十」匹配成功
        var words = new[] { W("六十九", 1.0, 3.0), W("很划算", 3.0, 5.0) };
        var lines = SentenceTimingAligner.Align("69。很划算。", words, 5, 6);
        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].Start is >= 0.99 and <= 1.01);
        Assert.True(lines[0].End <= 3.01);
    }

    [Fact]
    public void Punctuation()
    {
        var lines = SentenceTimingAligner.Align("一句话？\n第二句！第三句。", Array.Empty<AlignWord>(), 9, 9);
        Assert.Equal(3, lines.Count);
    }
}
