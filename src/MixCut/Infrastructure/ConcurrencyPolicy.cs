namespace MixCut.Infrastructure;

/// <summary>
/// 统一的并发策略。所有跑 ffmpeg / whisper-cli 的批处理路径都从这里取并发数，
/// 避免散落在 ImportViewModel / ExportView / SettingsWindow 三处各算各的导致不一致。
///
/// 设计原则：
/// 1. CPU 核心数是基准。
/// 2. 有 GPU 编码（NVENC/QSV/AMF）时给导出 +3 路加成，但封顶 11
///    （消费级 NVIDIA NVENC session 上限通常 3-5 路，超出会失败）。
/// 3. 有 GPU 解码（cuda/qsv/d3d11va）时给分析 +1 路（whisper 仍是 CPU 瓶颈，仅 ffmpeg 部分受益）。
/// 4. 没硬件时维持原 CPU 公式。
///
/// 让 Settings UI 显示具体公式，用户看得到「8 路（CPU 5 + GPU 加成 +3）」这样的透明信息。
/// </summary>
public static class ConcurrencyPolicy
{
    /// <summary>视频分析并发数（每路跑 whisper + ffmpeg 场景检测 / 静音检测）。</summary>
    public static int MaxAnalyzeConcurrency(int videosToAnalyzeCount = int.MaxValue)
    {
        var cores = Environment.ProcessorCount;
        var ffmpegBoost = HardwareEncoderProbe.DecodeHwaccel is not null ? 1 : 0;
        // 基础：N/4（whisper 单进程吃 N-2 线程，多路并发会互相挤）
        // GPU 解码 +1：场景检测的 ffmpeg 走 GPU 解码，让 1 路出 CPU 给 whisper
        // 上限 4（有 GPU）/ 3（无 GPU）
        var ceil = ffmpegBoost > 0 ? 4 : 3;
        var raw = cores / 4 + ffmpegBoost;
        var bounded = Math.Min(ceil, Math.Max(1, raw));
        return Math.Min(bounded, Math.Max(1, videosToAnalyzeCount));
    }

    /// <summary>
    /// 批量导出并发数。
    /// <para>
    /// v0.11（对齐 macOS v0.7.x / issue #14）：<b>所有导出一律串行，恒返回 1</b>。
    /// 原因：单条导出就吃满硬件编码 + 拼接的 CPU/GPU，多路并发会占满机器、互相抢资源反而更慢、
    /// 还可能让 app 卡顿（用户明确要求「所有的导出都不再并发了，一个一个导就完了」）。
    /// 串行后机器始终留余量、进度可预期，任意时刻只有 1 个 ffmpeg 编码进程在跑。
    /// </para>
    /// <para>
    /// 保留 <paramref name="tasksCount"/> / <paramref name="outputPixels"/> 参数只为兼容既有调用点签名，
    /// 不再参与计算（此前按 GPU 厂商 + 4K 内存封顶的自适应公式已废弃，见 git 历史）。
    /// </para>
    /// </summary>
    public static int MaxExportConcurrency(int tasksCount = int.MaxValue, long outputPixels = 0) => 1;

    /// <summary>给 Settings / 诊断 UI 用的导出并发说明文本。导出已一律串行。</summary>
    /// <param name="outputPixels">保留兼容签名，不再参与计算。</param>
    public static string ExplainExportFormula(long outputPixels = 0)
        => "串行（一条一条导，避免多路并发占满机器抢资源）";

    /// <summary>给 Settings UI 用的「分析并发透明拆解」文本。</summary>
    public static string ExplainAnalyzeFormula()
    {
        var cores = Environment.ProcessorCount;
        var basePart = Math.Max(1, cores / 4);
        var hwDecode = HardwareEncoderProbe.DecodeHwaccel is not null;
        var ceil = hwDecode ? 4 : 3;
        var raw = basePart + (hwDecode ? 1 : 0);
        var actual = Math.Min(ceil, raw);

        if (!hwDecode)
        {
            return $"{actual} 路（CPU N/4 = {basePart}，无 GPU 解码加速）";
        }
        return $"{actual} 路（CPU {basePart} + GPU 解码加成 +1，上限 {ceil}）";
    }
}
