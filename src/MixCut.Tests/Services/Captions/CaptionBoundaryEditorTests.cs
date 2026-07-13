using System.Collections.Generic;
using System.Linq;
using MixCut.Models;
using MixCut.Services.Captions;
using Xunit;

namespace MixCut.Tests.Services.Captions;

/// <summary>逐句字幕联动分界测试（移植 macOS CaptionBoundaryEditorTests，含 §4.4 全部用例）。</summary>
public class CaptionBoundaryEditorTests
{
    /// <summary>左句 "你好"(0~1)、右句 "世界"(1~2)，逐字均匀。</summary>
    private static List<CaptionLine> TwoLines() => new()
    {
        new CaptionLine("你好", 0, 1, new List<TimedChar> { new("你", 0.5), new("好", 1.0) }),
        new CaptionLine("世界", 1, 2, new List<TimedChar> { new("世", 1.5), new("界", 2.0) }),
    };

    [Fact]
    public void ExtendMergesOneChar()
    {
        var outLines = CaptionBoundaryEditor.MoveBoundary(TwoLines(), 0, 1.5);
        Assert.Equal(2, outLines.Count);
        Assert.Equal("你好世", outLines[0].Text);
        Assert.Equal(3, outLines[0].Chars.Count);
        Assert.True(System.Math.Abs(outLines[0].End - 1.5) < 0.001);
        Assert.Equal("界", outLines[1].Text);
        Assert.True(System.Math.Abs(outLines[1].Start - 1.5) < 0.001);
    }

    [Fact]
    public void FullCoverDeletesRight()
    {
        var outLines = CaptionBoundaryEditor.MoveBoundary(TwoLines(), 0, 2.0);
        Assert.Single(outLines);
        Assert.Equal("你好世界", outLines[0].Text);
        Assert.Equal(4, outLines[0].Chars.Count);
        Assert.True(System.Math.Abs(outLines[0].End - 2.0) < 0.001);
    }

    [Fact]
    public void ShrinkMovesCharsRight()
    {
        var merged = CaptionBoundaryEditor.MoveBoundary(TwoLines(), 0, 2.0); // 单句"你好世界"
        var outLines = CaptionBoundaryEditor.MoveBoundary(TwoLines(), 0, 0.5);
        Assert.Single(merged);
        Assert.Equal(2, outLines.Count);
        Assert.Equal("你", outLines[0].Text);         // "好"(end=1.0)>0.5 迁到右句
        Assert.Equal("好世界", outLines[1].Text);
        Assert.True(System.Math.Abs(outLines[0].End - 0.5) < 0.001);
    }

    [Fact]
    public void NeverEmptyLeft()
    {
        var outLines = CaptionBoundaryEditor.MoveBoundary(TwoLines(), 0, -5, 0.05);
        Assert.Equal(2, outLines.Count);
        Assert.False(string.IsNullOrEmpty(outLines[0].Text));
        Assert.Equal("你好", outLines[0].Text);        // 左句原样，不被掏空
    }

    [Fact]
    public void OutOfRangeNoop()
    {
        var src = TwoLines();
        Assert.True(CaptionBoundaryEditor.MoveBoundary(src, 1, 1.5).SequenceEqual(src));  // 末句无下一句
        Assert.True(CaptionBoundaryEditor.MoveBoundary(src, -1, 1.5).SequenceEqual(src));
    }

    [Fact]
    public void SplitLineAtMidpoint()
    {
        // 拆第 0 句「你好」(你0.5/好1.0) → 中点1：左「你」[0,0.5]、右「好」[0.5,1.0]，原第2句后移
        var outLines = CaptionBoundaryEditor.SplitLine(TwoLines(), 0);
        Assert.Equal(3, outLines.Count);
        Assert.Equal("你", outLines[0].Text);
        Assert.True(System.Math.Abs(outLines[0].End - 0.5) < 0.001);
        Assert.Equal("好", outLines[1].Text);
        Assert.True(System.Math.Abs(outLines[1].Start - 0.5) < 0.001);
        Assert.True(System.Math.Abs(outLines[1].End - 1.0) < 0.001);
        Assert.Equal("世界", outLines[2].Text);
    }

    [Fact]
    public void SplitSingleCharNoop()
    {
        var lines = new List<CaptionLine> { new CaptionLine("你", 0, 1, new List<TimedChar> { new("你", 1.0) }) };
        Assert.True(CaptionBoundaryEditor.SplitLine(lines, 0).SequenceEqual(lines)); // 单字不可拆
    }

    [Fact]
    public void LegacyNoChars()
    {
        var lines = new List<CaptionLine>
        {
            new CaptionLine("你好", 0, 1),   // Chars 空
            new CaptionLine("世界", 1, 2),
        };
        var outLines = CaptionBoundaryEditor.MoveBoundary(lines, 0, 1.5);
        Assert.Equal("你好世", outLines[0].Text);
        Assert.Equal("界", outLines[1].Text);
    }
}
