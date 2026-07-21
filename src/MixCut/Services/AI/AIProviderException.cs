namespace MixCut.Services.AI;

/// <summary>AI 提供商错误种类。对应 macOS 版 AIProviderError。</summary>
public enum AIProviderErrorKind
{
    ApiKeyNotConfigured,
    RequestFailed,
    InvalidResponse,
    RateLimited,
    JsonParsingFailed,
    ClientError,
    InsufficientBalance,
}

/// <summary>
/// AI 提供商调用异常。
///
/// <b>Message 只放人话，技术细节一律走 <see cref="TechnicalDetail"/>（仅供日志）。</b>
/// 这个约定必须守住：<c>ExceptionTranslator</c> 对本异常是**直接透传 Message** 的
/// （因为"它本身已经是中文人话"），所以只要工厂方法往 Message 里拼了
/// <c>（HTTP 500: {"error":...}）</c>，那段英文 JSON 就会原样出现在用户的错误弹窗里 ——
/// 这正是之前发生的事：方案生成、AI 重识别台词、叙事结构生成三条线全中，
/// 而它们恰好是新用户第一天最容易撞上的（Key 填错 / 额度用完）。
/// </summary>
public sealed class AIProviderException : Exception
{
    public AIProviderErrorKind Kind { get; }

    /// <summary>接口原始报文等技术细节。<b>仅供日志，绝不展示给用户。</b></summary>
    public string? TechnicalDetail { get; }

    public AIProviderException(AIProviderErrorKind kind, string message, string? technicalDetail = null)
        : base(message)
    {
        Kind = kind;
        TechnicalDetail = technicalDetail;
    }

    public static AIProviderException ApiKeyNotConfigured(AIProviderType provider) =>
        new(AIProviderErrorKind.ApiKeyNotConfigured,
            $"还没有配置{provider.DisplayName()}的 API Key。请到「设置 → AI 模型」填写后重试。");

    public static AIProviderException RequestFailed(string detail) =>
        new(AIProviderErrorKind.RequestFailed,
            "连不上 AI 服务，请检查网络后重试。若网络正常，可能是服务商暂时故障，稍后再试即可。",
            detail);

    /// <summary>4xx 客户端错误（模型名 / 接口地址 / 参数配置错），不可重试 —— 重试也是同样结果，
    /// 应立即提示用户检查配置，而非误导成「网络问题」干等指数退避。</summary>
    public static AIProviderException ClientError(int statusCode, string detail) =>
        new(AIProviderErrorKind.ClientError,
            "AI 拒绝了这次请求，通常是模型名称或接口地址填错了。请到「设置 → AI 模型」核对后重试。",
            $"HTTP {statusCode}: {detail}");

    /// <summary>
    /// 4xx 用统一分类器翻译成人话（免费额度耗尽 / 未开通模型权限 / 无效 Key / 欠费 / 限流…）。
    /// 识别出确切原因就用人话文案，识别不出退回通用「检查配置」。都归 <see cref="AIProviderErrorKind.ClientError"/>
    /// —— 这些都是确定性配置错误，重试无意义，与原 ClientError 同属不可重试集合。
    /// <paramref name="rawBody"/> 仅用于分类器关键字匹配，<paramref name="redactedSnippet"/> 只进日志，两者都不外露。
    /// </summary>
    public static AIProviderException Classified(int statusCode, string rawBody, string redactedSnippet)
    {
        var hint = ApiErrorClassifier.Hint(rawBody);
        return hint is null
            ? ClientError(statusCode, redactedSnippet)
            : new AIProviderException(AIProviderErrorKind.ClientError,
                hint,
                $"HTTP {statusCode}: {redactedSnippet}");
    }

    /// <summary>HTTP 402 Payment Required —— AI 服务商账户余额不足（如 DeepSeek「Insufficient Balance」）。
    /// 协议含义就是「需要付费」，绝不能归类成「网络问题」误导用户去查 WiFi / 切 VPN / 重启路由器，
    /// 真正的下一步是去服务商后台充值。不可重试。</summary>
    public static AIProviderException InsufficientBalance() =>
        new(AIProviderErrorKind.InsufficientBalance,
            "AI 服务账户余额不足，请前往你的 AI 服务商（如通义千问 / DeepSeek）后台充值后重试。");

    public static AIProviderException InvalidResponse(string detail) =>
        new(AIProviderErrorKind.InvalidResponse,
            "AI 返回的内容看不懂，多半是这次输出被截断了。请重试；若连续失败，可到「设置 → AI 模型」换一个更大的模型。",
            detail);

    public static AIProviderException RateLimited() =>
        new(AIProviderErrorKind.RateLimited,
            "AI 请求过于频繁（触发限流），请等待约 1 分钟后重试。");

    /// <summary>
    /// AI 没按 JSON 格式返回。
    ///
    /// 注意这里的参数<b>本身就是给用户看的人话</b>（由 <c>BuildJsonParseErrorDetail</c> 构造，
    /// 会带上模型实际返回的开头片段，好让用户判断该换哪个模型），
    /// 所以直接作为 Message，不要塞进只进日志的 TechnicalDetail —— 那样会把这条最有用的
    /// 排查线索藏起来，用户只剩一句无从下手的「格式异常」。
    /// </summary>
    public static AIProviderException JsonParsingFailed(string userFacingMessage) =>
        new(AIProviderErrorKind.JsonParsingFailed, userFacingMessage);
}
