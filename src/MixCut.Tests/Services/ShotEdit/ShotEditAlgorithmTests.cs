using System.Collections.Generic;
using System.Linq;
using MixCut.Services.ShotEdit;
using Xunit;

namespace MixCut.Tests.Services.ShotEdit;

/// <summary>
/// #12 分镜头切分 / 编辑纯算法回归测试（对齐 macOS PRD 01 §7.2 不变式 + §7.3 边界条件）。
/// 覆盖：贪心切分 ≥0.5s、首尾无缝铺满、最小 5 帧 clamp、合并/拆分。
/// </summary>
public class ShotEditAlgorithmTests
{
    // ---- 切分引擎 ----

    [Fact]
    public void Split_NoCuts_SingleShotCoveringWholeWindow()
    {
        var shots = ShotSegmentationEngine.Split(0, 5.0, new List<double>(), fps: 30);
        Assert.Single(shots);
        Assert.Equal(0, shots[0].StartFrame);
        Assert.Equal(150, shots[0].EndFrame);      // 5s × 30fps
        Assert.Equal(1, shots[0].OrderIndex);
    }

    [Fact]
    public void Split_SeamlessAndTotalPreserved()
    {
        // 分镜 [0,5s]，切点 1.0/2.0/3.0/4.0 → 5 段，首尾无缝铺满、总帧 = 150。
        var shots = ShotSegmentationEngine.Split(0, 5.0, new[] { 1.0, 2.0, 3.0, 4.0 }, fps: 30);
        Assert.Equal(5, shots.Count);
        Assert.Equal(0, shots.First().StartFrame);
        Assert.Equal(150, shots.Last().EndFrame);
        for (var i = 0; i < shots.Count - 1; i++)
        {
            Assert.Equal(shots[i].EndFrame, shots[i + 1].StartFrame);   // 无缝
        }
        Assert.Equal(150, shots.Sum(s => s.EndFrame - s.StartFrame));   // 总长恒等
    }

    [Fact]
    public void Split_RejectsCutsTooCloseTogether()
    {
        // 切点 1.0 / 1.2（相距 0.2s < 0.5s）→ 1.2 被拒；4.9（距末 0.1s < 0.5s）被拒 → 只接受 1.0。
        var shots = ShotSegmentationEngine.Split(0, 5.0, new[] { 1.0, 1.2, 4.9 }, fps: 30);
        Assert.Equal(2, shots.Count);   // [0,1.0] + [1.0,5.0]
        Assert.Equal(30, shots[0].EndFrame);
    }

    [Fact]
    public void Split_InvalidInputs_ReturnEmpty()
    {
        Assert.Empty(ShotSegmentationEngine.Split(0, 5, new[] { 1.0 }, fps: 0));
        Assert.Empty(ShotSegmentationEngine.Split(5, 5, new[] { 1.0 }, fps: 30));
    }

    // ---- 编辑器 ----

    private static IReadOnlyList<ShotSpan> Spans(params (int, int)[] xs) =>
        xs.Select(x => new ShotSpan(x.Item1, x.Item2)).ToList();

    [Fact]
    public void Merge_CombinesAdjacent()
    {
        var result = ShotPartitionEditor.Merge(Spans((0, 30), (30, 60), (60, 90)), 0);
        Assert.Equal(2, result.Count);
        Assert.Equal(new ShotSpan(0, 60), result[0]);
        Assert.Equal(new ShotSpan(60, 90), result[1]);
    }

    [Fact]
    public void MoveBoundary_ClampsToMinFrames()
    {
        // 边界 0 在 [0,60] 与 [60,120] 之间；尝试移到 2（< start+5=5）→ clamp 到 5。
        var result = ShotPartitionEditor.MoveBoundary(Spans((0, 60), (60, 120)), 0, toFrame: 2);
        Assert.Equal(5, result[0].EndFrame);
        Assert.Equal(5, result[1].StartFrame);
        Assert.Equal(120, result[1].EndFrame);          // 尾不变、无缝
    }

    [Fact]
    public void MoveBoundary_NormalMoveKeepsSeamlessAndTotal()
    {
        var result = ShotPartitionEditor.MoveBoundary(Spans((0, 60), (60, 120)), 0, toFrame: 40);
        Assert.Equal(40, result[0].EndFrame);
        Assert.Equal(40, result[1].StartFrame);
        Assert.Equal(120, result.Last().EndFrame);
    }

    [Fact]
    public void Split_MidpointHalvesShot()
    {
        var result = ShotPartitionEditor.SplitAtMidpoint(Spans((0, 100)), 0);
        Assert.Equal(2, result.Count);
        Assert.Equal(new ShotSpan(0, 50), result[0]);
        Assert.Equal(new ShotSpan(50, 100), result[1]);
    }

    [Fact]
    public void Split_TooCloseToEdge_ReturnsUnchanged()
    {
        // 拆分点 3（< start+5）→ 无效，原样返回。
        var input = Spans((0, 100));
        var result = ShotPartitionEditor.Split(input, 0, atFrame: 3);
        Assert.Single(result);
        Assert.Equal(new ShotSpan(0, 100), result[0]);
    }
}
