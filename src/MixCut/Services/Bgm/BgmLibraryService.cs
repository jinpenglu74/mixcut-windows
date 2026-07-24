using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.Bgm;

/// <summary>库中一条 BGM。显示名 = 文件名。</summary>
public sealed record BgmItem(string FileName, string FullPath, double DurationSeconds);

/// <summary>单个文件上传失败的原因（人话，直接展示给用户）。</summary>
public sealed record BgmUploadFailure(string FileName, string Reason);

/// <summary>一批上传的结果：成功条数 + 逐条失败明细（不许只报数量，见 issue #22 §3.1）。</summary>
public sealed record BgmUploadResult(int SuccessCount, IReadOnlyList<BgmUploadFailure> Failures);

/// <summary>
/// 全局 BGM 库（issue #22，对应 mac BGMLibraryStore）。
/// 纯文件目录即库：<see cref="AppPaths.BgmDirectory"/> 下的音频文件，列表 = 目录扫描
/// （文件名自然排序，与资源管理器一致），不动数据库 schema，所有项目共享。
/// </summary>
public sealed class BgmLibraryService
{
    /// <summary>可接收的音频扩展名（文件选择器与目录扫描共用）。</summary>
    public static readonly string[] AudioExtensions =
        { ".mp3", ".m4a", ".wav", ".aac", ".flac", ".ogg", ".wma" };

    /// <summary>上传磁盘空间预检的安全余量（issue #22 §3.1：源大小 + 500MB > 剩余 → 拒收）。</summary>
    private const long DiskMarginBytes = 500L * 1024 * 1024;

    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<BgmLibraryService> _logger;

    public BgmLibraryService(FFmpegRunner ffmpeg, ILogger<BgmLibraryService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>目录扫描（自然排序）。只列文件名/路径，不 probe 时长（时长由 <see cref="ListAsync"/> 补）。</summary>
    public IReadOnlyList<string> ListFiles()
    {
        try
        {
            return Directory.EnumerateFiles(AppPaths.BgmDirectory)
                .Where(f => AudioExtensions.Contains(
                    Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, NaturalComparer.Instance)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BgmDiag] 扫描 BGM 库目录失败");
            return Array.Empty<string>();
        }
    }

    /// <summary>库列表（含时长，ffprobe 逐个读取，FFmpegRunner 内部有缓存）。</summary>
    public async Task<IReadOnlyList<BgmItem>> ListAsync(CancellationToken ct = default)
    {
        var items = new List<BgmItem>();
        foreach (var path in ListFiles())
        {
            ct.ThrowIfCancellationRequested();
            var dur = await _ffmpeg.ProbeDurationAsync(path, ct);
            items.Add(new BgmItem(Path.GetFileName(path), path, dur));
        }
        return items;
    }

    /// <summary>
    /// 上传一批音频到库（逐个校验，单个失败不中断整批）：
    /// ① 有音轨且时长 > 0；② 磁盘空间预检（+500MB 余量）；③ 重名自动加「 2」「 3」后缀。
    /// </summary>
    public async Task<BgmUploadResult> UploadAsync(
        IReadOnlyList<string> sourcePaths, CancellationToken ct = default)
    {
        var success = 0;
        var failures = new List<BgmUploadFailure>();
        foreach (var src in sourcePaths)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(src);
            try
            {
                // ① 是有效音频：存在音轨且时长 > 0（视频文件带音轨也接受 —— 与 mac 行为一致，取其音轨）
                var hasAudio = await _ffmpeg.ProbeHasAudioAsync(src, ct);
                var duration = hasAudio ? await _ffmpeg.ProbeDurationAsync(src, ct) : 0;
                if (!hasAudio || duration <= 0)
                {
                    failures.Add(new BgmUploadFailure(name, "不是有效的音频文件，或文件已损坏"));
                    continue;
                }

                // ② 磁盘空间预检
                var srcSize = new FileInfo(src).Length;
                var root = Path.GetPathRoot(AppPaths.BgmDirectory);
                if (!string.IsNullOrEmpty(root))
                {
                    var free = new DriveInfo(root).AvailableFreeSpace;
                    if (srcSize + DiskMarginBytes > free)
                    {
                        failures.Add(new BgmUploadFailure(name,
                            $"磁盘空间不足：该文件约 {FormatSize(srcSize)}，当前磁盘仅剩 {FormatSize(free)}"));
                        continue;
                    }
                }

                // ③ 重名自动加后缀 + 复制入库
                var target = UniqueFileNamer.MakeUnique(AppPaths.BgmDirectory, name);
                File.Copy(src, Path.Combine(AppPaths.BgmDirectory, target));
                success++;
                _logger.LogInformation("[BgmDiag] 上传入库: {Src} → {Target} dur={Dur:F1}s",
                    name, target, duration);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BgmDiag] 上传失败: {Src}", src);
                failures.Add(new BgmUploadFailure(name, "复制文件失败，请检查文件是否被其它程序占用后重试"));
            }
        }
        return new BgmUploadResult(success, failures);
    }

    /// <summary>删除库内一条 BGM。删除后无法恢复；已导出的视频不受影响。</summary>
    public bool Delete(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
            _logger.LogInformation("[BgmDiag] 已删除: {Path}", Path.GetFileName(fullPath));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BgmDiag] 删除失败: {Path}", fullPath);
            return false;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F1} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:F0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    /// <summary>文件名自然排序（「歌 2」排在「歌 10」前），与资源管理器一致。</summary>
    private sealed class NaturalComparer : IComparer<string?>
    {
        public static readonly NaturalComparer Instance = new();

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string a, string b);

        public int Compare(string? a, string? b) =>
            StrCmpLogicalW(a ?? string.Empty, b ?? string.Empty);
    }
}
