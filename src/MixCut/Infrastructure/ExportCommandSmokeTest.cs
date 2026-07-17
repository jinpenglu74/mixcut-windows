using System.Diagnostics;

namespace MixCut.Infrastructure;

/// <summary>
/// issue #4 Phase 3：用「导出命令实际会拼的 codec 私有选项」跑一次真实 1 秒编码 smoke。
///
/// 为什么 <see cref="HardwareEncoderProbe"/> 的 smoke 不够：它只验证「能编 1 帧 <c>-c:v encoder</c>」，
/// 不覆盖导出额外拼的 <c>-b:v/-maxrate/-bufsize/-tag:v/-movflags</c> 等参数。某个私有选项被新版 ffmpeg
/// 拒识时（v0.4.0 的 <c>-allow_sw 0</c> 就是这样），HardwareEncoderProbe 照样绿，但真实导出会
/// 「Unrecognized option」→ exit -1414549496 → 0/N 全崩，用户看到英文错误码却无从自救。
///
/// 这里用与 <c>FFmpegRunner</c> 导出硬件分支<b>逐参数一致</b>的命令跑 testsrc：
/// 通过 → 记日志放行；失败 → 把硬件编码器降级为 CPU(libx264，一定能跑) + 亮 banner 告诉用户已切软件编码。
/// 启动期跑一次（与 HardwareEncoderProbe 同阶段，testsrc 1 秒、开销可忽略）。
/// </summary>
public static class ExportCommandSmokeTest
{
    /// <summary>smoke 是否通过（无硬件编码器时视为 true —— 本来就走 CPU，无需测）。</summary>
    public static bool Passed { get; private set; } = true;

    /// <summary>是否因 smoke 失败而把导出降级到了 CPU 编码（供 UI 亮 banner 提示用户）。</summary>
    public static bool DidFallbackToCpu { get; private set; }

    /// <summary>失败时记录的编码器名（如 h264_nvenc），用于日志/诊断。</summary>
    public static string? FailedCodec { get; private set; }

    /// <summary>smoke 是否已跑完（供 UI 等待结果再决定是否提示，避免读到未初始化状态）。</summary>
    public static bool Completed { get; private set; }

    /// <summary>启动期调用一次（必须在 <see cref="HardwareEncoderProbe.EagerInit"/> 之后）。</summary>
    public static void Run()
    {
        try
        {
            if (!BundledBinaries.FfmpegAvailable) return;

            var codec = HardwareEncoderProbe.H264Hardware;
            if (codec is null)
            {
                // 本来就没有硬件编码器 → 导出走 CPU，无需 smoke。
                Serilog.Log.Information("[ExportSmoke] 无硬件编码器，跳过（导出走 CPU 编码）");
                return;
            }

            if (SmokeTestExportArgs(codec, out var detail))
            {
                Passed = true;
                Serilog.Log.Information("[ExportSmoke] codec={Codec} passed", codec);
            }
            else
            {
                // 导出实际参数不被 ffmpeg 接受 → 降级 CPU，保证导出一定能跑。
                HardwareEncoderProbe.DisableHardwareEncoder();
                Passed = false;
                DidFallbackToCpu = true;
                FailedCodec = codec;
                Serilog.Log.Warning(
                    "[ExportSmoke] codec={Codec} FAILED → 已切回 CPU(libx264) 编码。detail={Detail}",
                    codec, detail);
            }
        }
        catch (Exception ex)
        {
            // smoke 本身异常绝不能影响启动；保守起见也降级 CPU（宁可编码慢，也别让导出全崩）。
            Serilog.Log.Warning(ex, "[ExportSmoke] 执行异常，保守起见切回 CPU 编码");
            HardwareEncoderProbe.DisableHardwareEncoder();
            Passed = false;
            DidFallbackToCpu = true;
        }
        finally
        {
            Completed = true;
        }
    }

    /// <summary>用导出硬件分支同款参数跑 testsrc，验证这套 codec 私有选项被当前 ffmpeg 接受。</summary>
    private static bool SmokeTestExportArgs(string codec, out string detail)
    {
        detail = string.Empty;
        var tmp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mixcut_export_smoke_" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var isHevc = codec.Contains("hevc", StringComparison.OrdinalIgnoreCase)
                         || codec.Contains("h265", StringComparison.OrdinalIgnoreCase);

            // —— 与 FFmpegRunner 导出硬件分支「逐参数一致」：这正是要验证被 ffmpeg 接受的那套私有选项 ——
            //    (-c:v / -b:v / -maxrate / -bufsize / -tag:v / -movflags，见 FFmpegRunner.cs 硬件编码分支)
            var args = new List<string>
            {
                "-v", "error",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30:duration=1",
                "-map", "0:v",
                "-c:v", codec,
                "-b:v", "4000k",
                "-maxrate", "8000k",
                "-bufsize", "8000k",
                "-tag:v", isHevc ? "hvc1" : "avc1",
                "-movflags", "+faststart",
                "-y", tmp,
            };

            var psi = new ProcessStartInfo
            {
                FileName = BundledBinaries.Ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi };
            p.Start();
            ChildProcessTracker.AddProcess(p);
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(true); } catch { /* 尽力 */ }
                detail = "timeout";
                return false;
            }
            if (p.ExitCode != 0)
            {
                detail = $"exit {p.ExitCode}: {stderr.Trim()}";
                return false;
            }
            var ok = System.IO.File.Exists(tmp) && new System.IO.FileInfo(tmp).Length > 0;
            if (!ok) detail = "输出文件为空/未产出";
            return ok;
        }
        finally
        {
            try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { /* 临时文件删失败无所谓 */ }
        }
    }
}
