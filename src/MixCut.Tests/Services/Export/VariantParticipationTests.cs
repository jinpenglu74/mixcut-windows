using System;
using System.Collections.Generic;
using System.Linq;
using MixCut.Models;
using MixCut.Services.Dubbing;
using MixCut.Services.Export;
using Xunit;

namespace MixCut.Tests.Services.Export;

/// <summary>
/// #13「配音变体池 · 参与排列组合」核心规则回归测试（对齐 macOS v0.7.x PRD 02 §3）。
/// 覆盖：档数公式（原版默认参与 / 变体默认不参与 opt-in / 锁定恒 1 / 兜底仅原版）、
/// 分镜任务展开器按参与标志出片、方案笛卡尔积按每槽 IncludeOriginal 采样。
/// </summary>
public class VariantParticipationTests
{
    // ---- 构造纯内存实体 ----

    private static SegmentDub Dub(int index, bool participates, bool hasAudio = true) => new()
    {
        Id = Guid.NewGuid(),
        TextVariantIndex = index,
        RewrittenText = $"变体{index}",
        AudioFilePath = hasAudio ? $"C:/fake/dub_{index}.m4a" : null,
        ParticipatesInCombination = participates,
        StatusRaw = nameof(SegmentDubStatus.Generated),
    };

    private static Segment Seg(bool locked, bool originalParticipates, params SegmentDub[] dubs)
    {
        var s = new Segment
        {
            Id = Guid.NewGuid(),
            IsVoiceLocked = locked,
            OriginalParticipatesInCombination = originalParticipates,
        };
        foreach (var d in dubs) { d.SegmentId = s.Id; s.SegmentDubs.Add(d); }
        return s;
    }

    // ---- 档数公式 Segment.CombinationSlotCount ----

    [Fact]
    public void SlotCount_Default_OnlyOriginal()
    {
        // 默认：原版参与(true) + 两个变体都没勾(false) → 只出原版，1 档（不炸）。
        var seg = Seg(locked: false, originalParticipates: true, Dub(0, false), Dub(1, false));
        Assert.Equal(1, seg.CombinationSlotCount);
        Assert.Empty(seg.CombinationDubVariants);
    }

    [Fact]
    public void SlotCount_OriginalPlusCheckedVariants()
    {
        // 原版参与 + 勾选 2 个变体 → 3 档。
        var seg = Seg(locked: false, originalParticipates: true, Dub(0, true), Dub(1, true), Dub(2, false));
        Assert.Equal(3, seg.CombinationSlotCount);
        Assert.Equal(2, seg.CombinationDubVariants.Count);
    }

    [Fact]
    public void SlotCount_OriginalOff_OnlyCheckedVariants()
    {
        // 原版不参与 + 勾选 2 个变体 → 2 档。
        var seg = Seg(locked: false, originalParticipates: false, Dub(0, true), Dub(1, true));
        Assert.Equal(2, seg.CombinationSlotCount);
    }

    [Fact]
    public void SlotCount_NothingChecked_FallbackToOne()
    {
        // 原版不参与 + 没勾任何变体 → 兜底回退仅原版 ×1，分镜不断档。
        var seg = Seg(locked: false, originalParticipates: false, Dub(0, false), Dub(1, false));
        Assert.Equal(1, seg.CombinationSlotCount);
    }

    [Fact]
    public void SlotCount_Locked_AlwaysOne()
    {
        // 锁定原声：忽略所有勾选，恒 1 档。
        var seg = Seg(locked: true, originalParticipates: true, Dub(0, true), Dub(1, true));
        Assert.Equal(1, seg.CombinationSlotCount);
    }

    [Fact]
    public void SlotCount_UngeneratedVariant_NotCounted()
    {
        // 勾选了但还没生成音频的变体不计入（EffectiveDubVariants 只含已生成音频者）。
        var seg = Seg(locked: false, originalParticipates: true, Dub(0, true, hasAudio: false));
        Assert.Equal(1, seg.CombinationSlotCount);
    }

    // ---- 分镜任务展开器 SegmentExportExpander ----

    private static SegmentExportSource Source(bool locked, bool includeOriginal, params int[] variantIndices)
        => new(Guid.NewGuid(), 1, "video", locked, includeOriginal,
            variantIndices.Select(i => new VariantRef(Guid.NewGuid(), i)).ToList());

    [Fact]
    public void Expand_OriginalOn_TwoVariants_ThreeItems()
    {
        var items = SegmentExportExpander.Expand(new[] { Source(false, true, 0, 1) });
        Assert.Equal(3, items.Count);
        Assert.Single(items, i => i.DubId is null);                 // 原版一条
        Assert.Equal(2, items.Count(i => i.DubId is not null));     // 变体两条
    }

    [Fact]
    public void Expand_OriginalOff_TwoVariants_NoOriginal()
    {
        var items = SegmentExportExpander.Expand(new[] { Source(false, false, 0, 1) });
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.DubId is null);         // 不出原版
    }

    [Fact]
    public void Expand_OriginalOff_NoVariants_FallbackOriginal()
    {
        // 兜底：都没勾 → 仍出原版一条，不断档。
        var items = SegmentExportExpander.Expand(new[] { Source(false, false) });
        Assert.Single(items);
        Assert.Null(items[0].DubId);
    }

    [Fact]
    public void Expand_Locked_OnlyOriginal()
    {
        var items = SegmentExportExpander.Expand(new[] { Source(true, true, 0, 1) });
        Assert.Single(items);
        Assert.Null(items[0].DubId);
    }

    // ---- 方案笛卡尔积 VariantCombinationGenerator（每槽 IncludeOriginal）----

    [Fact]
    public void Generate_PerSlotIncludeOriginal()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // 槽1：原版参与 + 1 变体 → 2 选项；槽2：原版不参与 + 1 变体 → 1 选项 → 共 2 条组合。
        var slots = new[]
        {
            new SlotOptions(false, true, new[] { a }),
            new SlotOptions(false, false, new[] { b }),
        };
        var result = VariantCombinationGenerator.Generate(slots, 256);
        Assert.Equal(2, result.FeasibleCount);
        Assert.Equal(2, result.Combinations.Count);
        // 槽2 恒选变体 b（原版没参与），两条组合里槽2 都是 b。
        Assert.All(result.Combinations, combo => Assert.Equal(b, combo[1]));
        // 槽1 一条原版(null)一条变体 a。
        Assert.Contains(result.Combinations, combo => combo[0] is null);
        Assert.Contains(result.Combinations, combo => combo[0] == a);
    }

    [Fact]
    public void Generate_AllUnchecked_FallbackOriginalOnly()
    {
        // 槽都没勾变体、原版也没参与 → 每槽兜底原版 → 唯一 1 条全原声组合。
        var slots = new[]
        {
            new SlotOptions(false, false, Array.Empty<Guid>()),
            new SlotOptions(false, false, Array.Empty<Guid>()),
        };
        var result = VariantCombinationGenerator.Generate(slots, 256);
        Assert.Equal(1, result.FeasibleCount);
        Assert.Single(result.Combinations);
        Assert.All(result.Combinations[0], choice => Assert.Null(choice));
    }
}
