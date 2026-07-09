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

    /// <summary>轮询间隔（秒）。</summary>
    private const int PollIntervalSeconds = 15;
    /// <summary>最大轮询次数（40 × 15s = 10 分钟超时）。</summary>
    private const int MaxPolls = 40;

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
    /// 提交画面替换任务并轮询到完成，返回结果视频 URL（http 自动升 https）。
    /// 失败抛 <see cref="ShotEditException"/>（人话）。
    /// </summary>
    public async Task<string> EditAsync(
        string videoFilePath, string prompt, Action<string>? onStatus = null, CancellationToken ct = default)
    {
        var key = _settings.GetApiKey(AIProviderType.Qwen);
        if (string.IsNullOrEmpty(key))
        {
            throw new ShotEditException("尚未配置千问 API Key，请到「设置」里填写后重试");
        }

        onStatus?.Invoke("上传中");
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

        // ---- 提交（异步任务）----
        string taskId;
        using (var req = new HttpRequestMessage(HttpMethod.Post, SubmitEndpoint))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("[ShotEditDiag] 提交失败 HTTP {Code}: {Body}", (int)resp.StatusCode, Trunc(respText));
                throw new ShotEditException("画面替换提交失败：" +
                    ApiErrorClassifier.Friendly($"HTTP {(int)resp.StatusCode} {respText}"));
            }
            taskId = ParseTaskId(respText)
                ?? throw new ShotEditException("画面替换提交异常：接口未返回任务 id");
        }
        _logger.LogInformation("[ShotEditDiag] 提交成功 taskId={TaskId}", taskId);
        onStatus?.Invoke("生成中");

        // ---- 轮询 ----
        for (var i = 0; i < MaxPolls; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct);

            using var req = new HttpRequestMessage(HttpMethod.Get, TaskEndpointBase + taskId);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var resp = await Http.SendAsync(req, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[ShotEditDiag] 轮询 HTTP {Code}: {Body}", (int)resp.StatusCode, Trunc(respText));
                continue; // 偶发抖动，继续轮询直到超时
            }

            var (status, videoUrl, failMsg) = ParseTaskStatus(respText);
            switch (status)
            {
                case "SUCCEEDED":
                    if (string.IsNullOrEmpty(videoUrl))
                    {
                        throw new ShotEditException("画面替换成功但未返回结果视频地址");
                    }
                    return UpgradeToHttps(videoUrl!);
                case "FAILED":
                    _logger.LogError("[ShotEditDiag] 任务失败: {Msg}", failMsg);
                    throw new ShotEditException("画面替换失败：" + ApiErrorClassifier.Friendly(failMsg ?? "task failed"));
                default:
                    onStatus?.Invoke("生成中");
                    break;
            }
        }
        throw new ShotEditException("画面替换生成超时（超过 10 分钟），请稍后重试");
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

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300];
}
