namespace MixCut.Services.AI;

/// <summary>
/// 把第三方 API（DashScope / 千问等 OpenAI 兼容、TTS、克隆、ASR）返回的原始英文错误文本，
/// 识别成用户能看懂的中文原因。对齐 mac <c>APIErrorClassifier</c>（AIProvider.swift）。
///
/// 关键设计（照搬 mac）：
/// - <b>顺序敏感</b>：免费额度耗尽 403 必须排在通用「模型未开通权限」403 之前，
///   否则付费档模型免费额度用完时会被误判成「未开通模型权限」，把用户往开克隆权限的错方向带。
/// - AI 改写 / TTS / 克隆 / ASR <b>共用一套</b>，避免各处重复翻译、口径漂移。
/// - 识别不出返回 null，由调用方兜底（保留原文截断），绝不把英文报错码直接甩用户脸上（§最高原则红线）。
/// </summary>
public static class ApiErrorClassifier
{
    /// <summary>识别原始错误文本的友好中文原因；无法识别返回 null。</summary>
    public static string? Hint(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var l = raw.ToLowerInvariant();

        // 欠费 / 余额不足（阿里云 Arrearage）—— 与网络无关，必须单独识别
        if (l.Contains("arrearage") || l.Contains("overdue") || l.Contains("欠费")
            || l.Contains("good standing") || l.Contains("insufficient balance"))
        {
            return "账户已欠费或余额不足：请到阿里云百炼（DashScope）控制台充值 / 检查额度后重试。";
        }
        // 克隆音色失效（多因更换 API Key，旧克隆音色绑定原账户）
        if (l.Contains("tts speak request failed")
            || (l.Contains("invalidparameter") && l.Contains("voice")))
        {
            return "克隆音色在当前 API Key 下不可用——通常是更换了千问 API Key（克隆音色绑定原账户）。"
                 + "请重新「一键改写」自动用当前 Key 重克隆原声；若仍失败，请确认该 Key 已开通 qwen3-tts-vc 声音克隆。";
        }
        // API Key 无效 / 未授权
        if (l.Contains("invalidapikey") || l.Contains("invalid api key")
            || l.Contains("invalid_api_key") || l.Contains("incorrect api key")
            || l.Contains("unauthorized") || l.Contains("http 401"))
        {
            return "API Key 无效或未授权：请到「设置」检查 API Key 是否填写正确、是否已开通对应服务（DashScope / 百炼）。";
        }
        // 免费额度耗尽 / 需开通付费（DashScope 对付费档模型返回 HTTP 403，message 为
        // "free quota has been exhausted ... complete your payment information (or disable
        // the \"use free tier only\" mode)"）。必须放在下面通用 403 分支之前。
        if (l.Contains("free quota") || l.Contains("free tier")
            || l.Contains("payment information")
            || (l.Contains("quota") && l.Contains("exhausted")))
        {
            return "所选模型的免费额度已用完：请到阿里云百炼（DashScope）控制台完成付费开通"
                 + "（或关闭「仅用免费额度 / use free tier only」模式），也可先换用仍有额度的模型"
                 + "（如 qwen-flash / qwen-turbo）后重试。此错误与声音克隆权限无关。";
        }
        // 模型未开通 / 无权限
        if (l.Contains("accessdenied") || l.Contains("model.access")
            || l.Contains("http 403") || (l.Contains("model") && l.Contains("denied")))
        {
            return "该 API Key 未开通所需模型权限，请在控制台开通对应模型后重试"
                 + "（声音克隆需 qwen-voice-enrollment 与 qwen3-tts-vc）。";
        }
        // 限流
        if (l.Contains("throttl") || l.Contains("ratelimit") || l.Contains("rate limit")
            || l.Contains("requests rate") || l.Contains("http 429"))
        {
            return "请求过于频繁（限流），请稍后再试。";
        }
        // 真正的网络问题
        if (l.Contains("timed out") || l.Contains("timeout") || l.Contains("offline")
            || l.Contains("network connection") || l.Contains("could not connect")
            || l.Contains("connection lost") || l.Contains("not connect to the internet"))
        {
            return "网络异常或超时，请检查网络后重试。";
        }
        return null;
    }

    /// <summary>
    /// **给用户看**：只出人话，任何情况下都不含接口原文。
    ///
    /// 这个方法替代了原来的 <c>Friendly</c>。<c>Friendly</c> 是个危险的设计 ——
    /// 它伪装成「翻译器」，实际上识别出原因时会把 200 字英文 JSON 拼在后面
    /// （`（接口返回：{"code":"InvalidApiKey","message":"Invalid API-key provided.",...}）`），
    /// 识别不出时更是直接返回英文原文一个字都不翻。它有 8 个调用点，
    /// 全都直通用户界面，是 CLAUDE.md「不许把英文报错丢给用户」红线最大的破口。
    /// 原文要看请走 <see cref="ForLog"/>，只进日志。
    /// </summary>
    public static string ForUser(string? raw) =>
        Hint(raw)
        ?? "AI 服务返回了异常，请稍后重试。若反复出现，请到「设置 → AI 模型」确认 API Key 是否有效、额度是否充足。";

    /// <summary>同上，从异常取文本。</summary>
    public static string ForUser(Exception ex) => ForUser(ex.Message);

    /// <summary>
    /// **仅供日志**：截断 + 脱敏后的接口原文，绝不能进 UI。
    /// 脱敏是因为部分网关的错误响应体会回显 Authorization 头（含用户密钥），
    /// 而日志会被「导出诊断包」打包发给开发者。
    /// </summary>
    public static string ForLog(string? raw, int maxRawLen = 500) => LogSanitizer.Safe(raw, maxRawLen);
}
