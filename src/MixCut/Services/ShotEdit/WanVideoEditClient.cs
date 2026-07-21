using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MixCut.Services.AI;
using MixCut.Utilities;

namespace MixCut.Services.ShotEdit;

/// <summary>分镜头 AI 画面替换失败（人话消息，供错误横幅展示）。</summary>
public sealed class ShotEditException : Exception
{
    public ShotEditException(string message) : base(message) { }
}

/// <summary>单次轮询（<see cref="WanVideoEditClient.PollOnceAsync"/>）的判定结果。</summary>
public enum PollOutcome
{
    /// <summary>任务成功，<c>VideoUrl</c> 有结果地址（24h 过期，需立即下载）。</summary>
    Succeeded,
    /// <summary>仍在跑（PENDING/RUNNING/暂时抖动），继续轮询。</summary>
    Running,
    /// <summary>任务失败（阿里 FAILED/CANCELED），<c>FailReason</c> 为人话原因。</summary>
    Failed,
    /// <summary>结果已过期 / 任务不存在（阿里返回 HTTP 200 + task_status=UNKNOWN，不是 404）。</summary>
    Expired,
}

/// <summary>一次轮询的只读结果（幂等查询，不计费）。</summary>
public readonly record struct PollResult(PollOutcome Outcome, string? VideoUrl, string? FailReason);

/// <summary>
/// 阿里 DashScope <c>wan2.7-videoedit</c> 画面替换客户端（异步任务：提交 → 轮询 → 返回结果 URL）。
/// 纯提示词、不框选、不传参考图；<c>duration:0</c> 让输出时长跟随输入（"进多少秒出多少秒"）。
/// 复用千问 DashScope key。对应 macOS Wan25VideoEditClient。
/// </summary>
public sealed class WanVideoEditClient
{
    /// <summary>实际提交的模型名（类名虽含 Wan，但 model 字符串以此为准）。</summary>
    public const string Model = "wan2.7-videoedit";

    private const string SubmitEndpoint =
        "https://dashscope.aliyuncs.com/api/v1/services/aigc/video-generation/video-synthesis";
    private const string TaskEndpointBase =
        "https://dashscope.aliyuncs.com/api/v1/tasks/";

    // 轮询上限/间隔已挪到 ShotVariantService（编排层决定 20 分钟超时后转 TimedOut）。
    // 本客户端只负责「提交拿 taskId」和「查一次」两个原子操作，不揉超时逻辑。

    // 提交请求体内联了 Base64 视频（2~10s @720P），体积可达数 MB，超时放大。
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly AppSettings _settings;
    private readonly ILogger<WanVideoEditClient> _logger;

    public WanVideoEditClient(AppSettings settings, ILogger<WanVideoEditClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// 只提交画面替换任务，返回 taskId（不等待结果）。缺 key / 提交 HTTP 失败 / 未返回 task_id
    /// 都抛 <see cref="ShotEditException"/>（人话）—— 这类「提交阶段失败」没拿到 taskId、也没扣费，
    /// 由调用方置 Failed(TaskId=null)。提交成功后 taskId 必须被调用方立刻落库（防超时丢失无法查回）。
    /// </summary>
    public async Task<string> SubmitAsync(string videoFilePath, string prompt, CancellationToken ct = default)
    {
        var key = _settings.GetApiKey(AIProviderType.Qwen);
        if (string.IsNullOrEmpty(key))
        {
            throw new ShotEditException("尚未配置千问 API Key，请到「设置」里填写后重试");
        }

        var base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(videoFilePath, ct));
        var dataUri = "data:video/mp4;base64," + base64;

        var body = new
        {
            model = Model,
            input = new
            {
                prompt,
                media = new[] { new { type = "video", url = dataUri } },
            },
            parameters = new
            {
                resolution = "720P",
                ratio = "9:16",
                duration = 0,          // 输出时长跟随输入（进多少秒出多少秒）
                watermark = false,
                prompt_extend = true,
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, SubmitEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("[ShotEditDiag] 提交失败 HTTP {Code}: {Body}", (int)resp.StatusCode, Trunc(respText));
            throw new ShotEditException("画面替换提交失败：" +
                ApiErrorClassifier.ForUser($"HTTP {(int)resp.StatusCode} {respText}"));
        }
        var taskId = ParseTaskId(respText)
            ?? throw new ShotEditException("画面替换提交异常：接口未返回任务 id");
        _logger.LogInformation("[ShotEditDiag] 提交成功 taskId={TaskId}", taskId);
        return taskId;
    }

    /// <summary>
    /// 查一次任务状态（幂等只读、不计费）。判定按阿里真实行为：
    /// 过期/不存在是 <c>HTTP 200 + task_status=UNKNOWN</c>（不是 404），必须读 task_status 判，别看状态码。
    /// 轮询上限交给上层（ShotVariantService），这里只映射「这一刻」的状态。
    /// </summary>
    public async Task<PollResult> PollOnceAsync(string taskId, CancellationToken ct = default)
    {
        var key = _settings.GetApiKey(AIProviderType.Qwen);
        if (string.IsNullOrEmpty(key))
        {
            return new PollResult(PollOutcome.Failed, null, "尚未配置千问 API Key");
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, TaskEndpointBase + taskId);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await Http.SendAsync(req, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // 偶发抖动（网关/CDN/限流）→ 当作仍在跑，让上层继续轮询；到点上层转 TimedOut，不误判失败。
            _logger.LogWarning("[ShotEditDiag] 轮询 HTTP {Code}: {Body}", (int)resp.StatusCode, Trunc(respText));
            return new PollResult(PollOutcome.Running, null, null);
        }

        var (status, videoUrl, failMsg) = ParseTaskStatus(respText);
        switch (status)
        {
            case "SUCCEEDED":
                return string.IsNullOrEmpty(videoUrl)
                    ? new PollResult(PollOutcome.Failed, null, "任务成功但未返回结果视频地址")
                    : new PollResult(PollOutcome.Succeeded, UpgradeToHttps(videoUrl!), null);
            case "FAILED":
            case "CANCELED":
                _logger.LogError("[ShotEditDiag] 任务失败: {Msg}", failMsg);
                return new PollResult(PollOutcome.Failed, null,
                    ApiErrorClassifier.ForUser(failMsg ?? "task failed"));
            case "UNKNOWN":
                // 阿里：任务不存在 / 已过期(超24h) / 非本账户 —— 旧结果拿不回了。
                return new PollResult(PollOutcome.Expired, null, null);
            default:
                // PENDING / RUNNING / 非 JSON 抖动(status=null) → 继续等。
                return new PollResult(PollOutcome.Running, null, null);
        }
    }

    private static string? ParseTaskId(string json)
    {
        // §红线：2xx 但正文非 JSON（网关维护页 / CDN 拦截页）时不能抛裸 JsonException 给用户，
        // 返回 null 让调用方给「接口未返回任务 id」的人话错误。
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("output", out var output)
                   && output.TryGetProperty("task_id", out var t)
                ? t.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static (string? Status, string? VideoUrl, string? FailMsg) ParseTaskStatus(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return (null, null, null); } // 非 JSON 响应当作抖动，继续轮询
        using (doc)
        {
        if (!doc.RootElement.TryGetProperty("output", out var output))
        {
            return (null, null, null);
        }
        var status = output.TryGetProperty("task_status", out var s) ? s.GetString() : null;
        var videoUrl = output.TryGetProperty("video_url", out var v) ? v.GetString() : null;
        var msg = output.TryGetProperty("message", out var m) ? m.GetString()
                : output.TryGetProperty("code", out var c) ? c.GetString()
                : null;
        return (status, videoUrl, msg);
        }
    }

    /// <summary>DashScope 结果 URL 多为 http(OSS)，升 https（V1 预签名不含 scheme，不影响签名）。</summary>
    private static string UpgradeToHttps(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url["http://".Length..]
            : url;

    /// <summary>截断 + **脱敏**：DashScope 网关的错误响应体可能回显 Authorization 头（含用户密钥），
    /// 而日志会被「导出诊断包」打包发给开发者。见 <see cref="AI.LogSanitizer"/>。</summary>
    private static string Trunc(string s) => AI.LogSanitizer.Safe(s, 300);
}
