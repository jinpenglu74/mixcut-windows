namespace MixCut.Models;

/// <summary>项目状态。对应 macOS 版 ProjectStatus（7 种）。</summary>
public enum ProjectStatus
{
    /// <summary>刚创建。</summary>
    Created,
    /// <summary>导入素材中。</summary>
    Importing,
    /// <summary>AI 分析中。</summary>
    Analyzing,
    /// <summary>分析完成，可生成方案。</summary>
    Ready,
    /// <summary>生成方案中。</summary>
    Generating,
    /// <summary>已完成。</summary>
    Completed,
    // 「已归档」已删除（issue #16）：归档后项目从列表消失且无从恢复，只会误伤用户。
    // 历史库里残留的 Archived 由启动期 RestoreArchivedProjects 统一恢复成 Completed。
}

public static class ProjectStatusExtensions
{
    private static readonly Dictionary<ProjectStatus, string> Labels = new()
    {
        [ProjectStatus.Created] = "新建",
        [ProjectStatus.Importing] = "导入中",
        [ProjectStatus.Analyzing] = "分析中",
        [ProjectStatus.Ready] = "就绪",
        [ProjectStatus.Generating] = "生成中",
        [ProjectStatus.Completed] = "已完成",
    };

    /// <summary>中文显示名。</summary>
    public static string ToLabel(this ProjectStatus status) => Labels[status];
}
