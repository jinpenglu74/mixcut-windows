using MixCut.Services.VideoProcessing;
using MixCut.ViewModels;
using Xunit;

namespace MixCut.Tests.ViewModels;

/// <summary>
/// ExceptionTranslator 按真因翻译 FFmpeg 失败的回归测试。
/// 背景：分析/导入/配音等非导出路径此前把所有 FFmpeg 失败一律翻成「文件可能损坏，请换视频」，
/// 而 4K OOM / 超时 的真因是机器内存不足 / 繁忙 —— 误诊成「换视频」是给用户错误指令（§最高原则红线）。
/// 修复后应复用 FFmpegException.Classify 的真实指纹分类，给准确、可操作的人话。
/// </summary>
public class ExceptionTranslatorTests
{
    [Fact]
    public void Oom_SaysMemory_NotFileCorruption()
    {
        // exit -12 (ENOMEM)：真因是内存不足，绝不能说「文件损坏 / 换个视频」。
        var msg = ExceptionTranslator.ToUserMessage(
            FFmpegException.ExecutionFailed(-12, "Cannot allocate memory"));
        Assert.Contains("内存", msg);
        Assert.DoesNotContain("损坏", msg);
        Assert.DoesNotContain("换一个视频", msg);
    }

    [Fact]
    public void Oom_ByStderrFingerprint_AlsoClassified()
    {
        // 关键行在 stderr 上方（不在最后 200 字），Classify 扫全文照样认得。
        var longTail = new string('x', 400);
        var stderr = "[vf#0:0] Error sending frames to consumers: Cannot allocate memory\n" + longTail;
        var msg = ExceptionTranslator.ToUserMessage(FFmpegException.ExecutionFailed(1, stderr));
        Assert.Contains("内存", msg);
    }

    [Fact]
    public void Timeout_SaysTimeout_NotFileCorruption()
    {
        var msg = ExceptionTranslator.ToUserMessage(
            FFmpegException.ExecutionFailed(-1, "进程超时"));
        Assert.Contains("超时", msg);
        Assert.DoesNotContain("损坏", msg);
    }

    [Fact]
    public void EncoderCrash_SaysCodec_NotFileCorruption()
    {
        var msg = ExceptionTranslator.ToUserMessage(
            FFmpegException.ExecutionFailed(-542398533, "received no packets"));
        Assert.Contains("编解码器", msg);
        Assert.DoesNotContain("损坏", msg);
    }

    [Fact]
    public void Other_KeepsFileCorruptionHint()
    {
        // 未知失败（如真损坏 / 不支持格式）仍给原有「文件可能损坏，换个视频」提示。
        var msg = ExceptionTranslator.ToUserMessage(
            FFmpegException.ExecutionFailed(1, "Invalid data found when processing input"));
        Assert.Contains("损坏", msg);
    }

    [Fact]
    public void BinaryNotFound_SaysReinstall()
    {
        // 组件缺失不能被归类成「文件损坏」—— 应保留「请重新安装应用」。
        var msg = ExceptionTranslator.ToUserMessage(FFmpegException.BinaryNotFound());
        Assert.Contains("重新安装", msg);
        Assert.DoesNotContain("损坏", msg);
    }

    [Theory]
    [InlineData(-12, "Cannot allocate memory")]
    [InlineData(-1, "进程超时")]
    [InlineData(-542398533, "received no packets")]
    [InlineData(1, "Invalid data found when processing input")]
    public void NeverLeaksExitCodeOrStderrKeyword(int exitCode, string stderr)
    {
        // 红线：用户看到的文案绝不含 exit code / "exit" / "stderr" 等原生痕迹。
        var msg = ExceptionTranslator.ToUserMessage(
            FFmpegException.ExecutionFailed(exitCode, stderr));
        Assert.DoesNotContain("exit", msg);
        Assert.DoesNotContain(exitCode.ToString(), msg);
        Assert.DoesNotContain("stderr", msg);
        Assert.False(string.IsNullOrWhiteSpace(msg));
    }
}
