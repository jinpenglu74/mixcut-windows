using System.ComponentModel.DataAnnotations.Schema;

namespace MixCut.Models;

/// <summary>
/// 物理镜头：一条逻辑分镜（<see cref="Segment"/>）内部按画面变化度切出的一段连贯镜头，有序、
/// 记录在源视频里的绝对帧区间。用户在「分镜头替换」工作区里对它做合并/拆分/移边界，
/// 或用 AI 生成画面变体（<see cref="ShotVariant"/>）替换其画面。对应 macOS 版 PhysicalShot @Model。
/// </summary>
public class PhysicalShot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // ---- 导航属性 ----

    public Guid? SegmentId { get; set; }
    public Segment? Segment { get; set; }

    // ---- 位置与帧区间（源视频绝对帧号，与所属分镜同一 fps 基准）----

    /// <summary>位置序，从 1 递增。占位式重组时顺序锁死。</summary>
    public int OrderIndex { get; set; } = 1;

    /// <summary>源视频绝对起始帧（inclusive）。</summary>
    public int StartFrame { get; set; }

    /// <summary>源视频绝对结束帧（exclusive）。</summary>
    public int EndFrame { get; set; }

    // ---- 占位选择 / 缩略图 ----

    /// <summary>该坑占位选定的变体 id；null = 用原分镜头画面。裸 Guid（非外键导航），删变体时手动置 null。</summary>
    public Guid? SelectedVariantId { get; set; }

    /// <summary>该镜头原画面首帧缩略图（按需生成）。</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>该镜头的 AI 画面变体（1 对多，级联删除）。</summary>
    public List<ShotVariant> Variants { get; set; } = new();

    // ---- 计算属性 ----

    /// <summary>帧数（保证非负）。</summary>
    [NotMapped]
    public int FrameCount => Math.Max(0, EndFrame - StartFrame);
}
