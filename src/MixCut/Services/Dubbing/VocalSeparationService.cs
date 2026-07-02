using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using MixCut.Infrastructure;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.Dubbing;

/// <summary>
/// 用内置 demucs.cpp 把一条视频的整轨音频分离成「人声 / 背景音乐」。对应 mac VocalSeparationService。
/// 整轨只分离一次并按 videoHash 缓存；导出按分镜切片，克隆用人声前 6s 做参考。
/// CLI：demucs.exe &lt;model.bin&gt; &lt;input.wav&gt; &lt;outDir&gt; → target_0_drums/1_bass/2_other/3_vocals.wav。
/// </summary>
public sealed class VocalSeparationService
{
    private const string ModelFileName = "ggml-htdemucs-4s.bin";

    /// <summary>模型下载源：国内镜像优先，回退国际源（铁律：勿用 huggingface 直链当唯一源）。</summary>
    private static readonly string[] ModelUrls =
    {
        "https://hf-mirror.com/datasets/Retrobear/demucs.cpp/resolve/main/ggml-model-htdemucs-4s-f16.bin",
        "https://huggingface.co/datasets/Retrobear/demucs.cpp/resolve/main/ggml-model-htdemucs-4s-f16.bin",
    };

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly SemaphoreSlim ModelDownloadGate = new(1, 1);

    /// <summary>
    /// 全局 demucs 串行信号量（对齐 ASRService 的 whisper 串行）。
    /// demucs.cpp 是纯 CPU、吃满所有核心的重活；同时跑多个会互相抢 CPU，<b>每个都更慢</b>（用户点两个
    /// 视频时实测两个 demucs 各跑 4 分钟还没完）。串行后每个独占 CPU，单个更快、总时长也不增加，
    /// 且 UI 能显示「排队中」而不是两个都干转。等待槽位的视频在 onProgress 报「排队等待人声分离…」。
    /// </summary>
    private static readonly SemaphoreSlim DemucsGate = new(1, 1);

    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<VocalSeparationService> _logger;

    public VocalSeparationService(FFmpegRunner ffmpeg, ILogger<VocalSeparationService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>整轨分离（已缓存直接返回）。<paramref name="onProgress"/> 回调进度文案。</summary>
    public async Task<SeparatedStems> SeparateAsync(
        string videoPath, string videoHash,
        IProgress<string>? onProgress = null, IProgress<double>? onPercent = null,
        CancellationToken ct = default)
    {
        var dir = AppPaths.StemsDirectory(videoHash);
        var vocals = Path.Combine(dir, "vocals.wav");
        var bgm = Path.Combine(dir, "bgm.wav");
        if (File.Exists(vocals) && File.Exists(bgm))
        {
            _logger.LogInformation("[DubDiag] 人声分离缓存命中 hash={Hash}", videoHash);
            return new SeparatedStems(vocals, bgm);
        }

        if (!BundledBinaries.DemucsAvailable)
        {
            throw new DubException("未找到人声分离组件（demucs），请重新安装应用");
        }

        var model = await EnsureModelAsync(onProgress, ct);

        var tmp = Path.GetTempPath();
        // 1) 抽整轨音频为 44.1k 立体声 wav（demucs.cpp 输入要求）
        onProgress?.Report("提取原始音频…");
        var inputWav = Path.Combine(tmp, $"mixcut-sep-in-{Guid.NewGuid():N}.wav");
        await _ffmpeg.RunAsync(
            new[] { "-y", "-i", videoPath, "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le", inputWav },
            timeout: TimeSpan.FromMinutes(5), cancellationToken: ct);

        // 2) demucs 分离到临时目录（4 stems）—— 全局串行：同时只跑一个 demucs，独占 CPU。
        var outDir = Path.Combine(tmp, $"mixcut-sep-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var probedDuration = await _ffmpeg.ProbeDurationAsync(videoPath, ct);

        // 等待串行槽位：若已有别的视频在分离，这里会阻塞，先告诉用户「排队中」别干等。
        if (DemucsGate.CurrentCount == 0)
        {
            onProgress?.Report("排队等待人声分离（同时只处理一个视频）…");
            _logger.LogInformation("[DubDiag] demucs 排队等待槽位 hash={Hash}", videoHash);
        }
        await DemucsGate.WaitAsync(ct);
        try
        {
            onProgress?.Report("AI 分离人声与背景音乐（较慢，请稍候）…");
            _logger.LogInformation("[DubDiag] demucs 开始分离 hash={Hash} dur={Dur:F0}s", videoHash, probedDuration);
            await RunDemucsAsync(model, inputWav, outDir, probedDuration, onPercent, ct);
        }
        finally
        {
            DemucsGate.Release();
        }

        var drums = Path.Combine(outDir, "target_0_drums.wav");
        var bass = Path.Combine(outDir, "target_1_bass.wav");
        var other = Path.Combine(outDir, "target_2_other.wav");
        var voc = Path.Combine(outDir, "target_3_vocals.wav");
        if (!File.Exists(voc))
        {
            throw new DubException("人声分离失败（未产出人声轨），请重试");
        }

        // 3) BGM = drums + bass + other（不归一化，保持原响度）；vocals 直接取
        onProgress?.Report("合成背景音乐轨…");
        await _ffmpeg.RunAsync(
            new[] { "-y", "-i", drums, "-i", bass, "-i", other,
                    "-filter_complex", "amix=inputs=3:normalize=0", "-c:a", "pcm_s16le", bgm },
            timeout: TimeSpan.FromMinutes(5), cancellationToken: ct);

        if (File.Exists(vocals)) { try { File.Delete(vocals); } catch { /* 占用忽略 */ } }
        File.Copy(voc, vocals, overwrite: true);

        // 清理临时
        TryDelete(inputWav);
        TryDeleteDir(outDir);

        _logger.LogInformation("[DubDiag] 人声分离完成 vocals={Vocals} bgm={Bgm}", vocals, bgm);
        return new SeparatedStems(vocals, bgm);
    }

    /// <summary>
    /// 从分离出的人声里取一段做克隆参考（默认 ≤6s，转 mp3 便于 base64）。
    /// 必须短而干净：参考太长(含多句原话)会让克隆 TTS 间歇性"续读参考内容"（坑4）。
    /// </summary>
    public async Task<string> ReferenceClipAsync(string vocalsPath, double maxSeconds = 6, CancellationToken ct = default)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"mixcut-clone-ref-{Guid.NewGuid():N}.mp3");
        // 保真：直接沿用分离出的人声（44.1k 立体声），不降采样/不并单声道 —— 对齐 mac referenceClip。
        // 曾错误加 `-ac 1 -ar 24000`：把参考砍成 24k 单声道 → 高频与音色细节丢失 → qwen 克隆出来
        // 像"机器朗读"而非原声。克隆保真度取决于参考音质，这里必须给足信息量。
        await _ffmpeg.RunAsync(
            new[] { "-y", "-i", vocalsPath, "-t", maxSeconds.ToString("F2"),
                    "-c:a", "libmp3lame", "-b:a", "128k", outPath },
            timeout: TimeSpan.FromMinutes(2), cancellationToken: ct);
        return outPath;
    }

    /// <summary>
    /// 从人声轨的 [startSeconds, startSeconds+maxSeconds) 区间取一段做「本分镜」克隆参考。
    /// 用于逐分镜单独克隆：每段用自己那段音频当参考，配音就跟本段原声一致（含性别/音色）。
    /// 同样保持 44.1k 立体声不降采样（保真），≤6s（避免克隆 TTS "续读参考内容"）。
    /// </summary>
    public async Task<string> SegmentReferenceClipAsync(
        string vocalsPath, double startSeconds, double maxSeconds, CancellationToken ct = default)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"mixcut-clone-segref-{Guid.NewGuid():N}.mp3");
        // -ss 放 -i 前：输入级 seek，跳到本分镜起点再截取，快且不用从头解码。
        await _ffmpeg.RunAsync(
            new[] { "-y", "-ss", Math.Max(0, startSeconds).ToString("F2"), "-i", vocalsPath,
                    "-t", maxSeconds.ToString("F2"), "-c:a", "libmp3lame", "-b:a", "128k", outPath },
            timeout: TimeSpan.FromMinutes(2), cancellationToken: ct);
        return outPath;
    }

    // ---- demucs 进程 ----

    /// <summary>
    /// demucs.cpp 把进度打成 "(NN.NNN%) ..." 行（如 "(83.333%) Time: decoder 0"）。
    /// 实测该百分比是<b>整段任务的全局单调进度</b>：即便内部把音频切成多个 segment 处理，% 也是
    /// 0→100 一路不回退（16s 片段实测 3 个 segment 仍只有一条 0→100 进度）。故 fraction = pct/100 直接用。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex DemucsProgressRegex =
        new(@"^\((\d+(?:\.\d+)?)%\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task RunDemucsAsync(string model, string input, string outDir, double durationSec,
        IProgress<double>? onPercent, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = BundledBinaries.Demucs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add(input);
        psi.ArgumentList.Add(outDir);

        using var process = new Process { StartInfo = psi };

        // 逐行解析进度并节流回调（变化 ≥0.5% 才上报，避免 135+ 次刷爆 UI 线程）。
        // 进度行可能落在 stdout 或 stderr，两路都解析。
        var lastReported = -1.0;
        var lastLoggedBucket = -1;
        void Parse(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var m = DemucsProgressRegex.Match(line);
            if (!m.Success) return;
            if (!double.TryParse(m.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct)) return;
            var frac = Math.Clamp(pct / 100.0, 0, 1);
            if (frac - lastReported >= 0.005 || frac >= 1.0)
            {
                lastReported = frac;
                onPercent?.Report(frac);
            }
            // 自验证用：每跨 20% 记一条，证明进度解析+事件读流确实在工作（grep [DemucsProgress]）。
            var bucket = (int)(pct / 20);
            if (bucket > lastLoggedBucket)
            {
                lastLoggedBucket = bucket;
                _logger.LogInformation("[DemucsProgress] {Pct:F0}%", pct);
            }
        }
        process.OutputDataReceived += (_, e) => Parse(e.Data);
        process.ErrorDataReceived += (_, e) => Parse(e.Data);

        process.Start();
        ChildProcessTracker.AddProcess(process); // 防孤儿：MixCut 退出时一并杀
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* 忽略 */ }
        // 事件式逐行读取既解析进度、又排空管道缓冲（防止塞满阻塞进程）。
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 超时按时长动态算（实测 CPU ~9x 实时，留足余量），下限 15 分钟。
        var timeout = TimeSpan.FromSeconds(Math.Max(900, durationSec * 30));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            throw new DubException($"人声分离超时（超过 {timeout.TotalMinutes:F0} 分钟）");
        }

        if (process.ExitCode != 0)
        {
            throw new DubException($"人声分离失败（demucs 退出码 {process.ExitCode}）");
        }
    }

    // ---- 模型下载（国内镜像 + Range 续传 + 原子落地；对齐 ASRService 思路）----

    private async Task<string> EnsureModelAsync(IProgress<string>? onProgress, CancellationToken ct)
    {
        var dest = Path.Combine(AppPaths.DemucsModelsDirectory, ModelFileName);
        if (File.Exists(dest) && new FileInfo(dest).Length > 0) return dest;

        await ModelDownloadGate.WaitAsync(ct);
        try
        {
            if (File.Exists(dest) && new FileInfo(dest).Length > 0) return dest;

            onProgress?.Report("下载人声分离模型（约 80MB，仅首次）…");
            Exception? last = null;
            foreach (var url in ModelUrls)
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        await DownloadWithResumeAsync(url, dest, onProgress, ct);
                        _logger.LogInformation("[DubDiag] demucs 模型就绪 {Path}", dest);
                        return dest;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        last = ex;
                        _logger.LogWarning("[DubDiag] demucs 模型下载失败({Url} 第{N}次): {Msg}", url, attempt + 1, ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
                    }
                }
            }
            throw new DubException("人声分离模型下载失败，请检查网络后重试", last ?? new Exception("unknown"));
        }
        finally
        {
            ModelDownloadGate.Release();
        }
    }

    private static async Task DownloadWithResumeAsync(
        string url, string dest, IProgress<string>? onProgress, CancellationToken ct)
    {
        var partPath = dest + ".download";
        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        // 416 = 已下完整个文件（服务端认为 range 不可满足）→ 视为完成
        if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            FinalizePart(partPath, dest);
            return;
        }
        resp.EnsureSuccessStatusCode();

        var append = resp.StatusCode == System.Net.HttpStatusCode.PartialContent && existing > 0;
        if (!append) existing = 0;

        var total = (resp.Content.Headers.ContentLength ?? 0) + (append ? existing : 0);
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fs = new FileStream(partPath, append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[1 << 16];
            long received = existing;
            long lastReport = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                received += n;
                if (total > 0 && received - lastReport >= 4 << 20) // 每 4MB 报一次
                {
                    lastReport = received;
                    onProgress?.Report($"下载人声分离模型 {received * 100.0 / total:F0}%（仅首次）…");
                }
            }
        }

        FinalizePart(partPath, dest);
    }

    private static void FinalizePart(string partPath, string dest)
    {
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(partPath, dest);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
