using System.Text.RegularExpressions;

namespace MixCut.Services.AI;

/// <summary>
/// 日志 / 错误文本脱敏（QW-14 的通用化）。
///
/// 威胁模型：部分 API 网关在错误响应体里**回显请求头**，其中包含
/// <c>Authorization: Bearer &lt;用户的付费 API Key&gt;</c>。这些响应体会被原样写进日志文件，
/// 而「设置 → 导出诊断包」会把日志打包成 zip 并引导用户发给开发者 —— 密钥就这样离开了用户的机器。
///
/// 原本只有 <see cref="OpenAICompatibleClient"/> 做了脱敏，四个 DashScope 客户端
/// （Wan 视频编辑 / 克隆 TTS / 声音克隆注册）都只截断不脱敏。统一收敛到这里。
/// </summary>
internal static class LogSanitizer
{
    private static readonly Regex BearerPattern =
        new(@"Bearer\s+[A-Za-z0-9\-\._]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SkKeyPattern =
        new(@"sk-[A-Za-z0-9\-]{6,}", RegexOptions.Compiled);

    /// <summary>抹掉文本里的 Bearer token 与 sk- 开头的密钥。</summary>
    public static string Redact(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return s ?? string.Empty;
        }
        s = BearerPattern.Replace(s, "Bearer ***");
        s = SkKeyPattern.Replace(s, "sk-***");
        return s;
    }

    /// <summary>截断到 <paramref name="max"/> 字符并脱敏。写日志前统一走这个。</summary>
    public static string Safe(string? s, int max = 500)
    {
        var t = s ?? string.Empty;
        if (t.Length > max)
        {
            t = t[..max];
        }
        return Redact(t);
    }
}
