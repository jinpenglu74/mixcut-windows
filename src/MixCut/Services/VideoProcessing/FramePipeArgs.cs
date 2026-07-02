using System.Globalization;

namespace MixCut.Services.VideoProcessing;

/// <summary>
/// hover 预览裸帧管道的 ffmpeg 参数拼接（纯函数，便于单测）。
///
/// 设计出发点：CLAUDE.md §兼容性总纲 —— 预览用我们自带的 ffmpeg.exe 进程外解码，
/// 与导出同源、彻底不碰系统编解码器（消灭 0xC00D109B）。这里只负责把命令行拼对，
/// 真正的进程/渲染在 <see cref="MixCut.Views.Components.FfmpegFramePlayer"/>。
/// </summary>
public static class FramePipeArgs
{
    private static string F(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);

    /// <summary>
    /// 视频裸帧管道参数：输出 bgra 像素流（WPF <c>WriteableBitmap</c> 直接可用）。
    /// 关键：<c>scale...decrease</c> 保宽高比缩入 W×H，再 <c>pad</c> 补黑边到**精确 W×H**，
    /// 这样每帧恒为 <c>W*H*4</c> 字节，读取端可按定长切帧（否则尺寸随源宽高比变化会读乱）。
    /// <c>-ss</c> 放在 <c>-i</c> 前用输入级快速 seek。
    /// </summary>
    public static string[] Video(string path, double start, double dur, int w, int h, int fps) => new[]
    {
        // 注：实测「减少探测」参数（-analyzeduration 0 / 小 probesize / nobuffer）反而拖慢 -ss 精确 seek
        // （深分镜首帧从 ~680ms 涨到 ~1950ms），已移除。保持默认探测 + 输入级快速 seek 最快。
        "-ss", F(start), "-i", path, "-t", F(dur), "-an",
        "-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease," +
               $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps={fps}",
        "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
    };

    /// <summary>
    /// 分镜预览专用：从源视频按整数帧裁切。EndFrame 为 exclusive，因此输出的最后画面必定是
    /// EndFrame-1，不会先越过边界再 seek 回来。
    /// </summary>
    public static string[] VideoFrames(
        string path, int startFrame, int endFrame, int w, int h) => new[]
    {
        "-i", path, "-an",
        "-vf", $"trim=start_frame={Math.Max(0, startFrame)}:end_frame={Math.Max(startFrame + 1, endFrame)}," +
               "setpts=PTS-STARTPTS," +
               $"scale={w}:{h}:force_original_aspect_ratio=decrease," +
               $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setsar=1",
        "-fps_mode", "passthrough",
        "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
    };

    /// <summary>
    /// 音频管道参数：s16le 立体声 44.1kHz PCM，喂给音频输出设备作主时钟（音画同步用）。
    /// </summary>
    public static string[] Audio(string path, double start, double dur) => new[]
    {
        "-ss", F(start), "-i", path, "-t", F(dur), "-vn",
        "-f", "s16le", "-ar", "44100", "-ac", "2", "pipe:1",
    };

    /// <summary>
    /// 配音换声预览音频管道（Point 4 方案预览换声）：克隆配音 dub（从头播）+ 分离背景乐 bgm
    /// （seek 到分镜在整片中的起点 <paramref name="bgmStart"/>）混音 → s16le PCM 喂声卡，替代原声。
    /// 与导出 <c>DubSegmentGraphBuilder</c> 同口径：bgm <c>volume=0.6</c>、
    /// <c>amix=inputs=2:duration=first:normalize=0</c>（输出长度跟 dub，画面/配音对齐）。
    /// <paramref name="bgmPath"/> 为 null（分离产物缺失）时只播配音、不混 BGM。
    /// </summary>
    public static string[] AudioDubBgm(string dubPath, string? bgmPath, double bgmStart, double dur)
    {
        if (string.IsNullOrEmpty(bgmPath))
        {
            return new[]
            {
                "-i", dubPath, "-vn",
                "-af", "aresample=44100",
                "-f", "s16le", "-ar", "44100", "-ac", "2", "pipe:1",
            };
        }
        return new[]
        {
            "-i", dubPath,
            "-ss", F(bgmStart), "-i", bgmPath, "-t", F(dur),
            "-filter_complex",
            "[0:a]aresample=44100[v];[1:a]aresample=44100,volume=0.6[b];" +
            "[v][b]amix=inputs=2:duration=first:normalize=0[a]",
            "-map", "[a]", "-vn",
            "-f", "s16le", "-ar", "44100", "-ac", "2", "pipe:1",
        };
    }

    /// <summary>一帧 bgra 的字节数（宽 × 高 × 4 通道）。</summary>
    public static int FrameBytes(int w, int h) => w * h * 4;
}
