using System.ComponentModel.DataAnnotations.Schema;

namespace MixCut.Models;

/// <summary>分镜头 AI 画面变体的生成状态。对应 macOS ShotVariantStatus。</summary>
public enum ShotVariantStatus
{
    /// <summary>刚创建、尚未提交。</summary>
    Pending,
    /// <summary>准备上传。</summary>
    Uploading,
    /// <summary>DashScope 生成中（异步任务轮询）。</summary>
    Generating,
    /// <summary>完成并已落地结果视频。</summary>
    Completed,
    /// <summary>
    /// 本地轮询超时，但云端任务可能仍在跑（阿里按任务成功计费，与客户端是否取回无关）。
    /// TaskId 保留 → 可用「重试」以同一 taskId 再查（不重复扣费）。对应 macOS timedOut。
    /// </summary>
    TimedOut,
    /// <summary>失败。</summary>
    Failed,
}

/// <summary>
/// 一个「分镜头（<see cref="PhysicalShot"/>）」的一次 AI 提示词画面编辑产物（可多条）。
/// 走阿里 DashScope wan2.7-videoedit：纯提示词、异步任务、约 2~4 分钟、按次计费。
/// 对应 macOS 版 SwiftData 的 ShotVariant @Model。
/// </summary>
public class ShotVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // ---- 导航属性 ----

    public Guid? ShotId { get; set; }
    public PhysicalShot? Shot { get; set; }

    // ---- 生成输入 ----

    /// <summary>用户提示词（纯文字描述怎么改画面，必填）。</summary>
    public string Prompt { get; set; } = string.Empty;

    // ---- 生成产物 / 状态 ----

    /// <summary><see cref="Status"/> 的底层字符串存储（数据库可读）。</summary>
    public string StatusRaw { get; set; } = nameof(ShotVariantStatus.Pending);

    /// <summary>DashScope 异步任务 id。</summary>
    public string? TaskId { get; set; }

    /// <summary>下载落地的结果视频路径（生成完成后有值）。</summary>
    public string? ResultVideoPath { get; set; }

    /// <summary>变体首帧缩略图路径。</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>失败原因（已翻译成人话 + 附接口原文，供错误横幅展示）。</summary>
    public string? FriendlyError { get; set; }

    /// <summary>创建时间（同一分镜头下多个变体按此升序排列）。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ---- 计算属性 ----

    /// <summary>生成状态。底层存 <see cref="StatusRaw"/> 字符串。</summary>
    [NotMapped]
    public ShotVariantStatus Status
    {
        get => Enum.TryParse<ShotVariantStatus>(StatusRaw, ignoreCase: true, out var s)
            ? s
            : ShotVariantStatus.Pending;
        set => StatusRaw = value.ToString();
    }

    /// <summary>是否完成且结果视频可用。</summary>
    [NotMapped]
    public bool IsUsable => Status == ShotVariantStatus.Completed && !string.IsNullOrEmpty(ResultVideoPath);
}
