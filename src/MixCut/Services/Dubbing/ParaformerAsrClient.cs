using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MixCut.Services.AI;
using MixCut.Utilities;

namespace MixCut.Services.Dubbing;

/// <summary>
/// 阿里百炼 paraformer-realtime-v2 流式 ASR 客户端。对应 macOS ParaformerASR.swift。
/// 协议：wss://dashscope.aliyuncs.com/api-ws/v1/inference/
///   1) send run-task JSON → 等 task-started
///   2) 流式 send PCM 二进制帧（3200 字节/帧，16k/mono/s16le）
///   3) send finish-task JSON → 等 task-finished → 累积 sentence_end 句拼接
/// 只替换台词文本，不动分镜边界。
/// </summary>
public sealed class ParaformerAsrClient
{
    private const string WsEndpoint = "wss://dashscope.aliyuncs.com/api-ws/v1/inference/";

    private readonly AppSettings _settings;
    private readonly ILogger<ParaformerAsrClient> _logger;

    public ParaformerAsrClient(AppSettings settings, ILogger<ParaformerAsrClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// 对一段 PCM 音频（16k/mono/s16le）做流式 ASR 识别，返回纯文本。
    /// 失败抛 <see cref="DubException"/>。
    /// </summary>
    public async Task<string> RecognizeAsync(byte[] pcm, CancellationToken ct = default)
    {
        var key = _settings.GetApiKey(AIProviderType.Qwen);
        if (string.IsNullOrEmpty(key))
            throw new DubException("尚未配置千问 API Key，请到「设置」里填写后重试");

        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"bearer {key}");
        ws.Options.SetRequestHeader("X-DashScope-DataInspection", "enable");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5)); // 长语音超时保护

        try
        {
            await ws.ConnectAsync(new Uri(WsEndpoint), cts.Token);
            if (ws.State != WebSocketState.Open)
                throw new DubException("Paraformer WebSocket 连接失败");

            var taskId = Guid.NewGuid().ToString();
            var finals = new List<string>();

            // Step 1: run-task
            var runTask = BuildRunTask(taskId);
            await SendText(ws, runTask, cts.Token);
            await WaitForEvent(ws, "task-started", finals, cts.Token);
            _logger.LogInformation("[ParaformerDiag] task-started，推送 {Bytes} 字节 PCM", pcm.Length);

            // Step 2: 流式推送 PCM（3200 字节/帧 ≈ 100ms）
            const int chunk = 3200;
            var offset = 0;
            while (offset < pcm.Length && !cts.Token.IsCancellationRequested)
            {
                var len = Math.Min(chunk, pcm.Length - offset);
                var frame = new byte[len];
                Array.Copy(pcm, offset, frame, 0, len);
                await SendBinary(ws, frame, cts.Token);
                offset += len;
                // 每 1 秒发一次日志（约 10 帧），避免刷屏
                if (offset % 32000 == 0)
                    _logger.LogTrace("[ParaformerDiag] 已推送 {Pct}% PCM", offset * 100 / pcm.Length);
            }
            _logger.LogTrace("[ParaformerDiag] PCM 推送完毕，发 finish-task");

            // Step 3: finish-task
            var finishTask = BuildFinishTask(taskId);
            await SendText(ws, finishTask, cts.Token);
            await WaitForEvent(ws, "task-finished", finals, cts.Token);

            var transcript = string.Join("", finals).Trim();
            if (string.IsNullOrEmpty(transcript))
                throw new DubException("ASR 识别返回空文本（请确认该片段有清晰人声）");

            _logger.LogInformation("[ParaformerDiag] 识别完成：「{Text}」", transcript.Length > 80 ? transcript[..80] + "…" : transcript);
            return transcript;
        }
        finally
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            ws.Dispose();
        }
    }

    private static string BuildRunTask(string taskId) =>
        "{\"header\":{\"action\":\"run-task\",\"task_id\":\"" + taskId + "\",\"streaming\":\"duplex\"}," +
        "\"payload\":{\"task_group\":\"audio\",\"task\":\"asr\",\"function\":\"recognition\"," +
        "\"model\":\"paraformer-realtime-v2\",\"parameters\":{\"format\":\"pcm\",\"sample_rate\":16000},\"input\":{}}}";

    private static string BuildFinishTask(string taskId) =>
        "{\"header\":{\"action\":\"finish-task\",\"task_id\":\"" + taskId + "\",\"streaming\":\"duplex\"},\"payload\":{\"input\":{}}}";

    private static async Task SendText(ClientWebSocket ws, string json, CancellationToken ct)
    {
        var buf = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(buf), WebSocketMessageType.Text, true, ct);
    }

    private static async Task SendBinary(ClientWebSocket ws, byte[] data, CancellationToken ct)
    {
        await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, ct);
    }

    /// <summary>持续接收 WS 消息，直到 header.event == target。期间累积 result-generated 的 sentence_end 句。</summary>
    private static async Task WaitForEvent(
        ClientWebSocket ws, string target, List<string> finals, CancellationToken ct)
    {
        while (true)
        {
            var msg = await ReceiveText(ws, ct);
            if (msg is null)
                throw new DubException("Paraformer WebSocket 连接被服务端关闭，未收到 " + target);

            using var doc = JsonDocument.Parse(msg);
            var root = doc.RootElement;
            if (!root.TryGetProperty("header", out var header) ||
                !header.TryGetProperty("event", out var evProp))
                continue;

            var ev = evProp.GetString() ?? "";
            if (ev == "task-failed")
            {
                var errMsg = root.TryGetProperty("payload", out var p)
                    && p.TryGetProperty("output", out var o)
                    && o.TryGetProperty("message", out var m)
                    ? m.GetString() : msg[..Math.Min(200, msg.Length)];
                // 原始英文体经分类器翻成人话（含前 200 字原文片段供排查）；此为 static 方法无 _logger，
                // 原文已随 DubException.Message 冒泡，上层 catch 会 LogError。
                throw new DubException("语音识别失败：" + MixCut.Services.AI.ApiErrorClassifier.Friendly(errMsg ?? "未知错误"));
            }

            if (ev == "result-generated")
            {
                if (root.TryGetProperty("payload", out var payload)
                    && payload.TryGetProperty("output", out var output)
                    && output.TryGetProperty("sentence", out var sentence)
                    && sentence.TryGetProperty("sentence_end", out var se) && se.GetBoolean()
                    && sentence.TryGetProperty("text", out var text))
                {
                    finals.Add(text.GetString() ?? "");
                }
            }

            if (ev == target) return;
        }
    }

    /// <summary>读一条完整 WS text 消息（拼合分片）；返回 null 表示连接关闭或非 text。</summary>
    private static async Task<string?> ReceiveText(ClientWebSocket ws, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage) return sb.ToString();
        }
    }
}
