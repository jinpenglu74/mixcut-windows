using System.IO;

namespace MixCut.Utilities;

/// <summary>
/// 应用数据目录集中管理。对应 macOS 版的 <c>~/Library/Application Support/MixCut</c>。
///
/// 三级写权限兜底（v0.4.0）：
/// 1. <c>%APPDATA%\MixCut\</c>            ← 默认（漫游配置目录）
/// 2. <c>%LOCALAPPDATA%\MixCut\</c>      ← 漫游不可写时（公司 GPO / 漫游目录坏）
/// 3. <c>&lt;EXE 目录&gt;\data\</c>       ← 都不行时（U 盘绿色运行 / 沙盒）
///
/// 启动期跑一遍写探针决定用哪一层，决策日志 <c>[AppDataDiag]</c>。
/// </summary>
public static class AppPaths
{
    /// <summary>应用根数据目录（三级兜底中第一个可写的）。</summary>
    public static string Root { get; } = ResolveWritableRoot();

    /// <summary>日志目录：<c>&lt;Root&gt;\logs</c>。</summary>
    public static string LogDirectory { get; } = CreateDir(Path.Combine(Root, "logs"));

    /// <summary>视频全局存储目录：<c>&lt;Root&gt;\Videos</c>。</summary>
    public static string VideosDirectory { get; } = CreateDir(Path.Combine(Root, "Videos"));

    /// <summary>
    /// Whisper 模型目录：优先 <c>%LOCALAPPDATA%\MixCut\whisper-models</c>，回退到 Root\whisper-models。
    /// 模型体积大，放本地（非漫游）。
    /// </summary>
    public static string WhisperModelsDirectory { get; } = ResolveWhisperModelsDir();

    /// <summary>
    /// Demucs 人声分离模型目录（v0.5.0 配音）：优先 <c>%LOCALAPPDATA%\MixCut\demucs-models</c>，
    /// 回退到 Root\demucs-models。模型 ~80MB，放本地（非漫游）。
    /// </summary>
    public static string DemucsModelsDirectory { get; } = ResolveLocalFirstDir("demucs-models");

    /// <summary>对齐后的配音音频（m4a）目录：<c>&lt;Root&gt;\Dubs</c>。</summary>
    public static string DubAudioDirectory { get; } = CreateDir(Path.Combine(Root, "Dubs"));

    /// <summary>
    /// 全局 BGM 库目录（issue #22，对齐 mac BGMLibraryStore）：<c>&lt;Root&gt;\BGM</c>。
    /// 纯文件目录即库（不动数据库 schema）：列表 = 目录扫描，显示名 = 文件名，所有项目共享。
    /// </summary>
    public static string BgmDirectory { get; } = CreateDir(Path.Combine(Root, "BGM"));

    /// <summary>SQLite 数据库文件路径：<c>&lt;Root&gt;\mixcut.db</c>。</summary>
    public static string DatabaseFile { get; } = Path.Combine(Root, "mixcut.db");

    /// <summary>
    /// 某视频的人声/BGM 分离产物目录（按内容哈希缓存，整轨只分离一次）：
    /// <c>&lt;Root&gt;\Stems\{videoHash}</c>。
    /// </summary>
    public static string StemsDirectory(string videoHash) =>
        CreateDir(Path.Combine(Root, "Stems", videoHash));

    /// <summary>
    /// 分镜头 AI 画面变体产物目录（#12，按视频哈希 + 镜头 id 缓存）：
    /// <c>&lt;Root&gt;\ShotVariants\{videoHash}\{shotId}</c>。存变体结果 mp4 + 缩略图 jpg。
    /// </summary>
    public static string ShotVariantsDirectory(string videoHash, Guid shotId) =>
        CreateDir(Path.Combine(Root, "ShotVariants", videoHash, shotId.ToString("N")));

    /// <summary>分镜头原画面首帧缩略图目录（#12）：<c>&lt;Root&gt;\ShotThumbnails\{videoHash}</c>。</summary>
    public static string ShotThumbnailsDirectory(string videoHash) =>
        CreateDir(Path.Combine(Root, "ShotThumbnails", videoHash));

    /// <summary>
    /// 分镜合成后的「替换画面」目录（#12，按视频哈希缓存）：
    /// <c>&lt;Root&gt;\ReplacedPictures\{videoHash}</c>。存 {segmentId}.mp4 + {segmentId}.jpg。
    /// </summary>
    public static string ReplacedPicturesDirectory(string videoHash) =>
        CreateDir(Path.Combine(Root, "ReplacedPictures", videoHash));

    // ---- 内部实现 ----

    /// <summary>用户是否把数据目录改到了自定义位置（非默认三级兜底）。模型等目录据此决定是否也跟着走。</summary>
    public static bool IsCustomRoot { get; private set; }

    private static string ResolveWritableRoot()
    {
        // 最优先：用户在设置里配置过的数据目录（指针文件，见 DataDirectoryMigrator）。
        // 注意：迁移本身在 App.OnStartup 最前面已执行完并写好指针，这里只负责「读指针决定 Root」。
        var configured = Infrastructure.DataDirectoryMigrator.ReadConfiguredRoot();
        if (!string.IsNullOrWhiteSpace(configured) && TryUse(configured))
        {
            IsCustomRoot = true;
            Console.WriteLine($"[AppDataDiag] selected=custom root={configured}");
            return configured;
        }

        var tier1 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MixCut");
        var tier2 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixCut");
        var tier3 = Path.Combine(AppContext.BaseDirectory, "data");

        var tries = new[]
        {
            ("tier1-Roaming", tier1),
            ("tier2-Local", tier2),
            ("tier3-AppDir", tier3),
        };

        foreach (var (tier, path) in tries)
        {
            if (TryUse(path))
            {
                // Serilog 启动期还没初始化，先 Console 输出，后续 EnvironmentDiagnostics 也会读 Root 字符串
                Console.WriteLine($"[AppDataDiag] selected={tier} root={path}");
                return path;
            }
        }

        // 三级全部失败 —— 极少见。返回 tier1（让后续操作显式失败，EnvironmentDiagnostics 会抓到）
        Console.WriteLine($"[AppDataDiag] ALL TIERS FAILED, fallback to {tier1}");
        return tier1;
    }

    private static bool TryUse(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveWhisperModelsDir() => ResolveLocalFirstDir("whisper-models");

    /// <summary>
    /// 大文件目录（模型）的位置：
    /// - 用户改过数据目录（<see cref="IsCustomRoot"/>）→ 直接放 <c>Root\{sub}</c>，让模型也搬到用户选的盘（省 C 盘）；
    /// - 默认情况 → 优先 <c>%LOCALAPPDATA%\MixCut\{sub}</c>（非漫游、不被同步），不可写回退 <c>Root\{sub}</c>。
    /// </summary>
    private static string ResolveLocalFirstDir(string sub)
    {
        if (IsCustomRoot)
            return CreateDir(Path.Combine(Root, sub));

        var localDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MixCut", sub);
        if (TryUse(localDir)) return CreateDir(localDir);
        return CreateDir(Path.Combine(Root, sub));
    }

    private static string CreateDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
