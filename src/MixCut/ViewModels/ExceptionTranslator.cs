using System.IO;
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
        // 分镜头 AI 画面替换（Wan）失败：构造时已是人话，直接用。
        MixCut.Services.ShotEdit.ShotEditException => ex.Message,
        // 语音识别：AsrException 的工厂消息本身就带「怎么办」（下载模型 / 重装应用 / 换机器）。
        MixCut.Services.ASR.AsrException => ex.Message,
        // 顺序要紧：TaskCanceledException 继承自 OperationCanceledException，必须排在前面，
        // 否则会被「已取消」吃掉。而两者语义相反 —— HttpClient 超时也抛 TaskCanceledException，
        // 把「服务器没响应」说成「已取消」会让用户以为是自己点了取消。
        TaskCanceledException tce when tce.InnerException is TimeoutException =>
            "请求超时，可能是网络较慢或服务繁忙，请稍后重试",
        OperationCanceledException => "已取消",

        // ↓ 以下都是原本落进「处理失败，请重试」这句笼统兜底里的常见真因。
        //   对用户来说「磁盘满了」和「文件被占用」的下一步动作完全不同，不能混成一句话。
        UnauthorizedAccessException =>
            "没有权限读写该文件，请确认文件未被设为只读，或换一个保存位置（避免系统盘根目录、C:\\Program Files 等受保护目录）",
        IOException io when IsDiskFull(io) =>
            "磁盘空间不足，请清理磁盘或把数据目录/导出位置换到别的盘后重试",
        IOException io when IsFileLocked(io) =>
            "文件正被其它程序占用，请关闭可能打开该视频的播放器 / 剪辑软件后重试",
        FileNotFoundException or DirectoryNotFoundException =>
            "找不到需要的文件，它可能已被移动或删除。请回到「素材导入」确认素材还在原位置",
        // 网络类：AI 调用、模型下载、配音合成都可能撞上。
        System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException =>
            "网络连接失败，请检查网络后重试；如果使用了代理 / VPN，也可能需要关掉再试",
        // AI 返回的内容不是合法 JSON —— 通常是模型能力不足或被内容风控截断。
        System.Text.Json.JsonException =>
            "AI 返回的内容无法解析，通常是所选模型能力不足或输出被截断。可在「设置 → AI 模型」换一个更强的模型后重试",

        // 兜底：真正没归类到的。技术细节只在日志里。
        _ => "处理失败，请重试；若反复出现，可到「设置 → 关于」导出诊断日志发给开发者",
    };

    /// <summary>磁盘满的判定：Win32 ERROR_DISK_FULL(0x70) / ERROR_HANDLE_DISK_FULL(0x27)。</summary>
    private static bool IsDiskFull(IOException ex)
    {
        var hr = ex.HResult & 0xFFFF;
        return hr is 0x70 or 0x27;
    }

    /// <summary>文件被占用的判定：ERROR_SHARING_VIOLATION(0x20) / ERROR_LOCK_VIOLATION(0x21)。</summary>
    private static bool IsFileLocked(IOException ex)
    {
        var hr = ex.HResult & 0xFFFF;
        return hr is 0x20 or 0x21;
    }

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
