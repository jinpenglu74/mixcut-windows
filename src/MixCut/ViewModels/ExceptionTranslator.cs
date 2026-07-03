using MixCut.Services.AI;
using MixCut.Services.VideoProcessing;

namespace MixCut.ViewModels;

/// <summary>
/// 把底层异常翻译成面向用户的人话提示。
///
/// CLAUDE.md 红线：用户面板上不许出现 exit code / stack trace / JsonException 等英文报错码。
/// ffmpeg 的 <c>exit -542398533</c>、whisper 的 <c>ExitCode=-1073741515</c>、原始 stderr 这类
/// 内容只能进日志，绝不直接拼进 <c>video.ErrorMessage</c> 给用户看。
///
/// 原始异常仍由各调用处的 <c>_logger.LogError</c> 记录进日志，排查信息不丢。
/// </summary>
public static class ExceptionTranslator
{
    /// <summary>把异常翻译成一句用户能看懂的中文原因（不含任何错误码 / stderr / 类型名）。</summary>
    public static string ToUserMessage(Exception ex) => ex switch
    {
        // AI 异常工厂（AIProviderException）本身已是纯中文人话，直接用。
        AIProviderException ai => ai.Message,
        // 组件缺失（BinaryNotFound）本就是人话（"请重新安装应用"），直接用 —— 不能被下面按真因归类成「文件损坏」。
        FFmpegException { Kind: FFmpegErrorKind.BinaryNotFound } bin => bin.Message,
        // 其余 FFmpeg 失败：Message 含 "exit {code}: {stderr}" 绝不能直给用户；且必须按真因分类 ——
        // OOM / 超时 的真因是「机器内存不够 / 繁忙」，不是文件坏，误诊成「换个视频」是给用户错误指令（§最高原则）。
        FFmpegException fx => FFmpegCauseMessage(fx),
        // DubException 的 Message 已由各配音调用处翻成人话（走 ApiErrorClassifier / 本就是中文），直接用。
        MixCut.Services.Dubbing.DubException => ex.Message,
        OperationCanceledException => "已取消",
        // 其余（含 whisper 的 ExitCode 异常、JsonException 等）统一兜底，技术细节只在日志里。
        _ => "处理失败，请重试（详情见日志）",
    };

    /// <summary>
    /// FFmpeg 失败的「原因」半句 —— 语境前缀（如「语音识别失败：」「本地分析失败：」）由调用处补全，这里只给中性的真因。
    /// 复用 <see cref="FFmpegException.Classify"/> 的真实指纹分类（客户 2026-06-18 4K OOM 事故沉淀），
    /// 避免把内存不足 / 超时 一律误诊成「文件损坏，请换视频」。文案不含任何 exit code / stderr。
    /// 注：导出路径另有 <see cref="MixCut.Services.Export.ExportErrorMessage.ToFriendly"/>（含「导出设置」等导出语境文案），
    /// 此处刻意用语境中性措辞，供导入/分析/配音等非导出场景复用。
    /// </summary>
    private static string FFmpegCauseMessage(FFmpegException fx) => FFmpegException.Classify(fx) switch
    {
        FFmpegFailureClass.Oom =>
            "内存不足，请关闭剪映、浏览器等占用内存的大型程序后重试（4K 素材尤其吃内存）",
        FFmpegFailureClass.Timeout =>
            "处理超时，可能是机器繁忙或素材规格过高，请稍后重试，或减少同时处理的视频数量",
        FFmpegFailureClass.EncoderCrash =>
            "视频编解码器异常，请重试；若反复失败可更新显卡驱动或更换一段素材",
        _ =>
            "视频处理失败，文件可能损坏或格式不支持，请换一个视频重试",
    };
}
