using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MixCut.Infrastructure;

/// <summary>
/// 让「数据存储目录」可被用户改到别的盘（默认全在 C 盘的 %APPDATA%，用户装 D 盘也没用 —— 这是修那个反人类设计）。
///
/// 为什么单独一个类、且**绝不引用 <see cref="MixCut.Utilities.AppPaths"/>**：
///   AppPaths.Root 是 static 一次性初始化，第一次被碰（Serilog 建日志 / 打开 DB）就永久冻结。
///   要让用户改的位置生效，必须在**任何 AppPaths 访问之前**：
///     1) 读「指针文件」决定 Root（<see cref="ReadConfiguredRoot"/>，AppPaths 启动时调它）；
///     2) 若有「待迁移」标记，先把旧目录整棵搬到新盘并改库里的绝对路径（<see cref="RunPendingIfAny"/>）。
///   这两步都只用 Environment 原生路径算，不碰 AppPaths，否则会触发它的静态初始化、前功尽弃。
///
/// 指针文件与待迁移标记放在**固定锚点**（%APPDATA%\MixCut\ 优先，不可写回退 %LOCALAPPDATA%\MixCut\），
/// 因为它们不能存在「正在被搬走的那个目录」里（鸡生蛋）。指针只是一行纯路径字符串，几十字节，留在漫游目录无妨；
/// 真正占空间的视频 / 模型 / 数据库才搬到用户选的盘。
/// </summary>
public static class DataDirectoryMigrator
{
    private const string PointerFileName = "data-root.txt";
    private const string PendingFileName = "pending-migration.json";
    private const string MigrationLogName = "migration.log";

    // 固定锚点（不经 AppPaths）。指针 / 待迁移标记 / 迁移日志都落这里。
    private static string RoamingAnchor =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MixCut");
    private static string LocalAnchor =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixCut");

    private static IEnumerable<string> Anchors => new[] { RoamingAnchor, LocalAnchor };

    // ---------------- 指针文件（决定 Root） ----------------

    /// <summary>读用户配置的数据根目录；没配过返回 null（→ AppPaths 走默认三级兜底）。</summary>
    public static string? ReadConfiguredRoot()
    {
        foreach (var anchor in Anchors)
        {
            try
            {
                var f = Path.Combine(anchor, PointerFileName);
                if (File.Exists(f))
                {
                    var p = File.ReadAllText(f).Trim();
                    if (p.Length > 0) return p;
                }
            }
            catch { /* 读不了就当没配 */ }
        }
        return null;
    }

    private static void WritePointer(string root)
    {
        foreach (var anchor in Anchors)
        {
            try
            {
                Directory.CreateDirectory(anchor);
                File.WriteAllText(Path.Combine(anchor, PointerFileName), root);
                return;
            }
            catch { /* 换下一个锚点 */ }
        }
    }

    private static void ClearPointer()
    {
        foreach (var anchor in Anchors)
        {
            try
            {
                var f = Path.Combine(anchor, PointerFileName);
                if (File.Exists(f)) File.Delete(f);
            }
            catch { /* 尽力 */ }
        }
    }

    /// <summary>复刻 AppPaths 的默认三级兜底（漫游→本地→EXE\data），供「恢复默认位置」和「当前是否默认」判断。</summary>
    public static string ComputeDefaultRoot()
    {
        var tier1 = RoamingAnchor;
        var tier2 = LocalAnchor;
        var tier3 = Path.Combine(AppContext.BaseDirectory, "data");
        foreach (var p in new[] { tier1, tier2, tier3 })
            if (CanWrite(p)) return p;
        return tier1;
    }

    private static bool CanWrite(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // ---------------- 待迁移标记（设置里点「更改位置」后写，重启时执行） ----------------

    private sealed class PendingDto
    {
        public string FromRoot { get; set; } = "";
        public string ToRoot { get; set; } = "";
        public bool ToIsDefault { get; set; }
        // 额外要搬的目录（主要是模型：它们默认在 %LOCALAPPDATA%，不在旧 Root 下）。src→dst 一一对应。
        public List<string> ExtraFrom { get; set; } = new();
        public List<string> ExtraTo { get; set; } = new();
    }

    public static bool HasPending()
    {
        foreach (var anchor in Anchors)
            if (File.Exists(Path.Combine(anchor, PendingFileName))) return true;
        return false;
    }

    /// <summary>
    /// 设置页调用：登记一次「下次启动把数据从 fromRoot 搬到 toRoot」。extraPairs 是模型等 Root 之外要一并搬的 (源, 目标)。
    /// 只写标记、不动数据 —— 真正的搬运在下次启动、任何文件被打开之前跑（避免 DB / 日志被占用锁死）。
    /// </summary>
    public static void RequestMigration(
        string fromRoot, string toRoot, bool toIsDefault,
        IEnumerable<(string from, string to)> extraPairs)
    {
        var dto = new PendingDto { FromRoot = fromRoot, ToRoot = toRoot, ToIsDefault = toIsDefault };
        foreach (var (f, t) in extraPairs)
        {
            dto.ExtraFrom.Add(f);
            dto.ExtraTo.Add(t);
        }
        var json = JsonSerializer.Serialize(dto);
        foreach (var anchor in Anchors)
        {
            try
            {
                Directory.CreateDirectory(anchor);
                File.WriteAllText(Path.Combine(anchor, PendingFileName), json);
                return;
            }
            catch { /* 换锚点 */ }
        }
    }

    private static PendingDto? ReadPending()
    {
        foreach (var anchor in Anchors)
        {
            try
            {
                var f = Path.Combine(anchor, PendingFileName);
                if (File.Exists(f))
                    return JsonSerializer.Deserialize<PendingDto>(File.ReadAllText(f));
            }
            catch { /* 损坏当没有 */ }
        }
        return null;
    }

    private static void ClearPending()
    {
        foreach (var anchor in Anchors)
        {
            try
            {
                var f = Path.Combine(anchor, PendingFileName);
                if (File.Exists(f)) File.Delete(f);
            }
            catch { /* 尽力 */ }
        }
    }

    // ---------------- 启动期执行迁移 ----------------

    public enum MigrationOutcome { NoPending, Success, Failed }

    public sealed record MigrationResult(MigrationOutcome Outcome, string? Error = null, string? ToRoot = null);

    /// <summary>
    /// 若有待迁移标记，就地执行：拷贝旧目录+模型到新位置 → 改库里 4 个绝对路径列 → 写指针 → 删旧目录 → 清标记。
    /// **必须在任何 AppPaths / 日志 / DB 访问之前调用**（App.OnStartup 最前面）。无标记时立即返回、零副作用。
    ///
    /// 安全策略：先「拷贝」不删源；改库、写指针成功后才删旧目录。中途失败 → 不写指针（应用继续用旧 Root）、
    /// 清掉半成品新目录、清标记（避免死循环），旧数据全程完好。progress(阶段文案, 0~1) 供 UI 显示。
    /// </summary>
    public static MigrationResult RunPendingIfAny(Action<string, double>? progress = null)
    {
        var pending = ReadPending();
        if (pending is null) return new MigrationResult(MigrationOutcome.NoPending);

        var logLines = new List<string>();
        void Log(string m) => logLines.Add($"{DateTime.Now:HH:mm:ss.fff} {m}");

        var from = pending.FromRoot;
        var to = pending.ToRoot;
        var createdTo = !Directory.Exists(to); // 记住是不是我们新建的，失败回滚只删自己建的
        try
        {
            Log($"begin migrate from='{from}' to='{to}' toIsDefault={pending.ToIsDefault} extra={pending.ExtraFrom.Count}");
            if (!Directory.Exists(from))
                throw new DirectoryNotFoundException($"源数据目录不存在：{from}");

            // 拷贝任务清单：
            //  - 主目录 from→to（**排除**模型子目录 + 迁移控制文件；模型由下面的额外任务按目标模式单独搬）；
            //  - 各额外任务（模型）：src→dst 一一对应，dst 已由设置页按「目标是自定义/默认」算好正确落点。
            var jobs = new List<(string src, string dst, bool excludeModels)> { (from, to, true) };
            for (var i = 0; i < pending.ExtraFrom.Count; i++)
            {
                var s = pending.ExtraFrom[i];
                if (i < pending.ExtraTo.Count && Directory.Exists(s)
                    && !PathEquals(s, pending.ExtraTo[i]))
                    jobs.Add((s, pending.ExtraTo[i], false));
            }

            // 估算总字节，用于进度百分比。
            long total = 0;
            foreach (var (src, _, _) in jobs) total += DirectorySize(src);
            if (total <= 0) total = 1;
            long copied = 0;

            foreach (var (src, dst, excludeModels) in jobs)
            {
                Log($"copy '{src}' -> '{dst}' excludeModels={excludeModels}");
                var exDirs = excludeModels ? ModelDirNames : null;
                var exFiles = excludeModels ? ControlFileNames : null;
                CopyDirectory(src, dst, (bytes, file) =>
                {
                    copied += bytes;
                    progress?.Invoke($"正在迁移数据… {Human(copied)} / {Human(total)}", Math.Min(0.92, (double)copied / total));
                }, exDirs, exFiles);
            }

            // 改库：4 个绝对路径列的旧 Root 前缀 → 新 Root 前缀。
            progress?.Invoke("正在更新数据库路径…", 0.95);
            var dbPath = Path.Combine(to, "mixcut.db");
            if (File.Exists(dbPath))
            {
                var rows = RewriteDbPaths(dbPath, from, to);
                Log($"db rewrite rows updated={rows}");
            }
            else Log("db not found at destination, skip rewrite");

            // 写指针（回默认则清指针）。此后 AppPaths 会解析到新 Root。
            if (pending.ToIsDefault) ClearPointer();
            else WritePointer(to);
            Log("pointer updated");

            // 成功后才删旧目录 + 旧模型（尽力，删不掉不影响 —— 指针已指向新位置）。
            progress?.Invoke("正在清理旧目录…", 0.98);
            TryDeleteDir(from, Log);
            foreach (var s in pending.ExtraFrom) TryDeleteDir(s, Log);

            ClearPending();
            Log("done OK");
            WriteMigrationLog(logLines);
            progress?.Invoke("迁移完成", 1.0);
            return new MigrationResult(MigrationOutcome.Success, ToRoot: to);
        }
        catch (Exception ex)
        {
            Log($"FAILED: {ex.GetType().Name}: {ex.Message}");
            // 回滚：没写指针 → 应用仍用旧 Root，旧数据完好。删掉我们建的半成品新目录。
            try { if (createdTo && Directory.Exists(to)) Directory.Delete(to, recursive: true); }
            catch (Exception rex) { Log($"rollback delete partial dest failed: {rex.Message}"); }
            ClearPending(); // 别让坏标记每次启动都重试
            WriteMigrationLog(logLines);
            return new MigrationResult(MigrationOutcome.Failed, Error: ex.Message);
        }
    }

    // ---------------- 工具 ----------------

    /// <summary>改 SQLite 里 4 个存绝对路径的列：把 fromRoot 前缀换成 toRoot（用 substr 前缀比对，避免 LIKE 通配符坑）。</summary>
    private static int RewriteDbPaths(string dbPath, string fromRoot, string toRoot)
    {
        var cols = new (string table, string col)[]
        {
            ("Videos", "LocalPath"),
            ("Videos", "ThumbnailPath"),
            ("Segments", "ThumbnailPath"),
            ("SegmentDubs", "AudioFilePath"),
        };
        var total = 0;
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        foreach (var (table, col) in cols)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                // NULL 列：substr(NULL,...) = NULL，WHERE 不成立，自然跳过。
                cmd.CommandText =
                    $"UPDATE \"{table}\" SET \"{col}\" = @to || substr(\"{col}\", @fromLen + 1) " +
                    $"WHERE substr(\"{col}\", 1, @fromLen) = @from;";
                cmd.Parameters.AddWithValue("@to", toRoot);
                cmd.Parameters.AddWithValue("@from", fromRoot);
                cmd.Parameters.AddWithValue("@fromLen", fromRoot.Length);
                total += cmd.ExecuteNonQuery();
            }
            catch { /* 某列/表不存在（老库）跳过，不阻断迁移 */ }
        }
        return total;
    }

    public static long DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long sum = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { sum += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return sum;
    }

    public static long GetDriveFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return -1;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return -1; }
    }

    // 主目录拷贝时排除的顶层子目录（模型 —— 单独按目标模式搬）与控制文件（指针 / 待迁移标记 / 迁移日志）。
    private static readonly HashSet<string> ModelDirNames =
        new(StringComparer.OrdinalIgnoreCase) { "whisper-models", "demucs-models" };
    private static readonly HashSet<string> ControlFileNames =
        new(StringComparer.OrdinalIgnoreCase) { PointerFileName, PendingFileName, MigrationLogName };

    /// <summary>
    /// 递归拷贝 src→dst。<paramref name="excludeTopDirs"/> / <paramref name="excludeTopFiles"/> 仅对 src 顶层生效
    /// （深层同名不排除），用于把模型子目录 + 迁移控制文件排除在主目录拷贝之外。
    /// </summary>
    private static void CopyDirectory(
        string src, string dst, Action<long, string> onFileCopied,
        HashSet<string>? excludeTopDirs = null, HashSet<string>? excludeTopFiles = null)
    {
        Directory.CreateDirectory(dst);

        foreach (var topDir in Directory.EnumerateDirectories(src))
        {
            var name = Path.GetFileName(topDir);
            if (excludeTopDirs != null && excludeTopDirs.Contains(name)) continue;
            CopyDirectory(topDir, Path.Combine(dst, name), onFileCopied); // 子目录全量拷（排除只在顶层）
        }

        foreach (var file in Directory.EnumerateFiles(src))
        {
            var name = Path.GetFileName(file);
            if (excludeTopFiles != null && excludeTopFiles.Contains(name)) continue;
            var target = Path.Combine(dst, name);
            File.Copy(file, target, overwrite: true);
            long len = 0;
            try { len = new FileInfo(target).Length; } catch { }
            onFileCopied(len, file);
        }
    }

    private static bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>child 是否在 parent 目录之下（或相等）。用于设置页判断模型目录要不要单独搬。</summary>
    public static bool IsUnder(string child, string parent)
    {
        try
        {
            var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
            var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(c, p, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void TryDeleteDir(string path, Action<string> log)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            log($"delete old dir failed (ignored) '{path}': {ex.Message}");
        }
    }

    private static void WriteMigrationLog(IEnumerable<string> lines)
    {
        foreach (var anchor in Anchors)
        {
            try
            {
                Directory.CreateDirectory(anchor);
                File.AppendAllLines(Path.Combine(anchor, MigrationLogName), lines);
                return;
            }
            catch { /* 换锚点 */ }
        }
    }

    private static string Human(long bytes)
    {
        double b = bytes;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {u[i]}";
    }

    // ---------------- 自验证（不碰真实锚点，只在临时沙盒里跑核心逻辑） ----------------

    /// <summary>
    /// 沙盒自测核心迁移逻辑（拷贝主目录排除模型 + 模型单独搬 + 改库路径列），**不触碰真实 %APPDATA% 指针**。
    /// 由 <c>--selftest-datamigrate</c> 调用，验证「文件搬对位置 + DB 绝对路径列被正确重写」。返回人话报告。
    /// </summary>
    public static string SelfTest(string sandbox)
    {
        var report = new List<string>();
        bool allOk = true;
        void Check(string name, bool ok) { report.Add($"  [{(ok ? "PASS" : "FAIL")}] {name}"); if (!ok) allOk = false; }

        var from = Path.Combine(sandbox, "from");
        var to = Path.Combine(sandbox, "to");
        var modelSrc = Path.Combine(sandbox, "localappdata-models", "whisper-models");
        var modelDst = Path.Combine(to, "whisper-models");
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);

        // 1) 造 from：视频文件 + 缩略图 + 库里一条带绝对路径的 Video 行；再造一个「Root 外」的模型目录。
        var videoPath = Path.Combine(from, "Videos", "hashabc", "a.mp4");
        var thumbPath = Path.Combine(from, "Thumbnails", "a.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(videoPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
        File.WriteAllText(videoPath, "fake-video-bytes");
        File.WriteAllText(thumbPath, "fake-thumb");
        Directory.CreateDirectory(modelSrc);
        File.WriteAllText(Path.Combine(modelSrc, "ggml.bin"), "fake-model");
        // 一个在 Root 之下、名为 whisper-models 的目录也造上，验证主拷贝确实排除它（不重复搬）。
        var inRootModel = Path.Combine(from, "whisper-models");
        Directory.CreateDirectory(inRootModel);
        File.WriteAllText(Path.Combine(inRootModel, "should-be-excluded.bin"), "x");

        var dbPath = Path.Combine(from, "mixcut.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText =
                "CREATE TABLE Videos(Id TEXT, LocalPath TEXT, ThumbnailPath TEXT);" +
                "INSERT INTO Videos VALUES('v1', @lp, @tp);";
            c.Parameters.AddWithValue("@lp", videoPath);
            c.Parameters.AddWithValue("@tp", thumbPath);
            c.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools(); // 释放 db 文件句柄，才能被拷贝

        // 2) 跑核心：主拷贝（排除模型）+ 模型单独搬 + 改库。
        CopyDirectory(from, to, (_, _) => { }, ModelDirNames, ControlFileNames);
        CopyDirectory(modelSrc, modelDst, (_, _) => { });
        var updated = RewriteDbPaths(Path.Combine(to, "mixcut.db"), from, to);

        // 3) 断言。
        Check("主目录视频已拷到 to", File.Exists(Path.Combine(to, "Videos", "hashabc", "a.mp4")));
        Check("主目录缩略图已拷到 to", File.Exists(Path.Combine(to, "Thumbnails", "a.jpg")));
        Check("Root 内 whisper-models 被主拷贝排除", !File.Exists(Path.Combine(to, "whisper-models", "should-be-excluded.bin")));
        Check("模型经额外任务搬到 to\\whisper-models", File.Exists(Path.Combine(modelDst, "ggml.bin")));
        Check("DB 重写影响了行数>0", updated > 0);

        var newDb = Path.Combine(to, "mixcut.db");
        using (var conn = new SqliteConnection($"Data Source={newDb}"))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT LocalPath, ThumbnailPath FROM Videos WHERE Id='v1';";
            using var r = c.ExecuteReader();
            r.Read();
            var lp = r.GetString(0);
            var tp = r.GetString(1);
            report.Add($"    LocalPath => {lp}");
            Check("LocalPath 前缀已换成 to", lp.StartsWith(to, StringComparison.OrdinalIgnoreCase) && File.Exists(lp));
            Check("ThumbnailPath 前缀已换成 to", tp.StartsWith(to, StringComparison.OrdinalIgnoreCase) && File.Exists(tp));
        }
        SqliteConnection.ClearAllPools();

        report.Insert(0, allOk ? "[DataMigrateSelfTest] ALL PASS" : "[DataMigrateSelfTest] SOME FAILED");
        try { Directory.Delete(sandbox, true); } catch { }
        return string.Join("\n", report);
    }
}
