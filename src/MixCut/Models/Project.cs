using System.ComponentModel.DataAnnotations.Schema;

namespace MixCut.Models;

/// <summary>项目。对应 macOS 版 SwiftData 的 Project @Model。</summary>
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public ProjectStatus Status { get; set; } = ProjectStatus.Created;

    /// <summary>用户自定义 AI Prompt（可选）。</summary>
    public string? CustomPrompt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    // ---- 导航属性 ----

    /// <summary>项目与视频的多对多关联（视频全局共享）。</summary>
    public List<ProjectVideo> ProjectVideos { get; set; } = new();

    /// <summary>混剪策略。</summary>
    public List<MixStrategy> Strategies { get; set; } = new();

    /// <summary>混剪方案。</summary>
    public List<MixScheme> Schemes { get; set; } = new();

    // ---- 计算属性（不映射到数据库）----

    /// <summary>关联的视频列表（含自建分镜载体，供内部引用/计数）。</summary>
    [NotMapped]
    public IEnumerable<Video> Videos =>
        ProjectVideos.Where(pv => pv.Video != null).Select(pv => pv.Video!);

    /// <summary>
    /// #17：对用户可见的「视频」列表 —— 排除自建分镜的载体视频（它们只在分镜库以分镜形态出现）。
    /// 项目概览 / 导入页的「已导入视频」列表与计数一律走这个，避免自建分镜和成片视频混在一起。
    /// </summary>
    [NotMapped]
    public IEnumerable<Video> VisibleVideos => Videos.Where(v => !v.IsUserUploaded);

    /// <summary>视频总数（含自建载体，仅内部引用用；对用户展示请用 <see cref="VisibleVideoCount"/>）。</summary>
    [NotMapped]
    public int VideoCount => ProjectVideos.Count;

    /// <summary>对用户可见的视频数（排除自建分镜载体）。概览/导入页显示用。</summary>
    [NotMapped]
    public int VisibleVideoCount => VisibleVideos.Count();

    /// <summary>分镜总数（自建分镜的整片分镜照常计入）。</summary>
    [NotMapped]
    public int SegmentCount => Videos.Sum(v => v.Segments.Count);

    /// <summary>方案总数。</summary>
    [NotMapped]
    public int SchemeCount => Schemes.Count;

    /// <summary>
    /// 用项目名作为字符串表示。
    /// 不只是为了好看：WPF 的 ListBoxItem 默认拿 ToString() 当无障碍名称，不重写的话
    /// 屏幕阅读器和自动化工具在项目列表里读到的全是「MixCut.Models.Project」。
    /// </summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "未命名项目" : Name;
}
