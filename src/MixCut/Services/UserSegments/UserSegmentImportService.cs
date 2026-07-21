using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Models;
using MixCut.Services.AI;
using MixCut.Services.ASR;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;
using MixCut.ViewModels;   // 复用 ImportViewModel.ComputeFileHash / NormalizeSemanticType（public static 工具）

namespace MixCut.Services.UserSegments;

/// <summary>一个自建分镜文件的处理结果状态。</summary>
public enum UserSegmentImportStatus
{
    Success,
    SkippedTooLong,     // 超过 15 秒
    SkippedDuplicate,   // 同 hash 已存在
    SkippedUnreadable,  // 无法读取时长/元数据
    Failed,
}

/// <summary>单个自建分镜文件的处理结果。</summary>
public readonly record struct UserSegmentImportResult(
    string FileName, UserSegmentImportStatus Status, Guid? VideoId, Guid? SegmentId, string? Message);

/// <summary>
/// #17 自建分镜上传：把用户在别的编辑器里剪好的成品分镜（≤15s、一文件=一分镜）落成
/// 「载体视频(IsUserUploaded) + 覆盖整片的分镜」，只做整片 ASR + AI「只打标不切分」，不切分。
/// 就绪后与普通分镜数据同构，下游全部零特殊化复用。对应 macOS 自建分镜流水线（TRD 05）。注册为单例。
/// </summary>
public sealed class UserSegmentImportService
{
    /// <summary>单条自建分镜时长上限（秒）。</summary>
    public const double MaxDurationSec = 15.0;

    private readonly IDbContextFactory<MixCutDbContext> _dbFactory;
    private readonly FFmpegRunner _ffmpeg;
    private readonly ASRService _asr;
    private readonly AIAnalysisService _ai;
    private readonly ILogger<UserSegmentImportService> _logger;

    public UserSegmentImportService(
        IDbContextFactory<MixCutDbContext> dbFactory, FFmpegRunner ffmpeg,
        ASRService asr, AIAnalysisService ai, ILogger<UserSegmentImportService> logger)
    {
        _dbFactory = dbFactory;
        _ffmpeg = ffmpeg;
        _asr = asr;
        _ai = ai;
        _logger = logger;
    }

    /// <summary>
    /// 处理一个文件：校验 → 落盘建载体视频/整片分镜 → 整片 ASR → 只打标 → 就绪。
    /// 每步独立容错：ASR 失败 → text 留空；打标失败 → 语义类型给默认「过渡」。不阻断其它文件。
    /// </summary>
    /// <param name="onSegmentCreated">占位分镜落库后立即回调其 id，供 UI 先显示 loading 占位卡。</param>
    /// <param name="onStage">处理阶段回调（"识别中"/"打标中"），供 UI 更新占位卡文案。</param>
    public async Task<UserSegmentImportResult> ImportOneAsync(
        string filePath, Guid projectId,
        Action<Guid>? onSegmentCreated = null,
        Action<string>? onStage = null, CancellationToken ct = default)
    {
        var name = Path.GetFileName(filePath);
        _logger.LogInformation("[UserSegDiag] 开始处理 file={File}", name);
        try
        {
            // 1) 时长校验 ≤15s。
            double dur;
            try { dur = await _ffmpeg.ProbeDurationAsync(filePath, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[UserSegDiag] 读取时长失败 file={File}", name);
                return new(name, UserSegmentImportStatus.SkippedUnreadable, null, null, "无法读取该文件（格式不支持或已损坏）");
            }
            if (dur <= 0) return new(name, UserSegmentImportStatus.SkippedUnreadable, null, null, "无法读取时长");
            if (dur > MaxDurationSec)
            {
                _logger.LogInformation("[UserSegDiag] 跳过（超时长）file={File} dur={Dur:F1}s", name, dur);
                return new(name, UserSegmentImportStatus.SkippedTooLong, null, null, $"时长 {dur:F1} 秒，超过 15 秒上限");
            }

            // 2) 去重：只在「本项目已有同文件的自建分镜」时才算重复跳过。
            //    撞普通导入素材 / 别项目的视频**不算重复** —— 自建分镜是独立分镜实体，允许与成片素材共存
            //    （物理文件按 hash 共享，ContentHash 索引非唯一）。之前误判「任何同 hash 都跳过」会把
            //    「用户既导入过成片、又想把它当自建分镜」的正常场景卡死（issue 复现：文件已在素材页导入过）。
            var hash = ImportViewModel.ComputeFileHash(filePath);
            await using (var dbDup = await _dbFactory.CreateDbContextAsync(ct))
            {
                // 还要求「该载体视频名下还有分镜」——删掉自建分镜后残留的孤儿载体视频不算重复，
                // 否则「上传→删除→再上传」会被误判已存在（用户实测复现）。
                var dup = hash is not null && await dbDup.Videos.AnyAsync(
                    v => v.ContentHash == hash && v.IsUserUploaded
                         && v.ProjectVideos.Any(pv => pv.ProjectId == projectId)
                         && v.Segments.Any(), ct);
                if (dup)
                {
                    _logger.LogInformation("[UserSegDiag] 跳过（本项目已有该自建分镜）file={File}", name);
                    return new(name, UserSegmentImportStatus.SkippedDuplicate, null, null, "该文件已作为自建分镜存在于本项目，已跳过");
                }
            }

            // 3) 落盘 + 建载体视频（读元数据 + 首帧缩略图）。
            var dest = FileHelper.CopyVideoToGlobal(filePath, hash ?? Guid.NewGuid().ToString("N"));
            var video = new Video
            {
                Name = Path.GetFileNameWithoutExtension(filePath),
                LocalPath = dest,
                ContentHash = hash,
                IsUserUploaded = true,
                Status = VideoStatus.Transcribing,   // 处理中
            };
            await ExtractMetadataAsync(video, ct);
            var fps = video.Fps > 0 ? video.Fps : 30.0;

            // 4) 覆盖整片的分镜（帧 0 .. round(dur*fps)）。
            var seg = new Segment
            {
                VideoId = video.Id,
                SegmentIndex = "seg_001",
                PositionType = PositionType.Middle,
            };
            seg.SetBoundsFrames(0, Math.Max(1, (int)Math.Round(video.Duration * fps)), fps);
            var thumbPath = Path.Combine(FileHelper.GlobalThumbnailDirectory, $"seg_{seg.Id}.jpg");
            try
            {
                await _ffmpeg.GenerateThumbnailAsync(dest, thumbPath, 0.1, ct);
                seg.ThumbnailPath = thumbPath;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[UserSegDiag] 首帧缩略图失败 seg={Id}", seg.Id); }

            var segmentId = seg.Id;
            var videoId = video.Id;
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            {
                db.Videos.Add(video);
                db.ProjectVideos.Add(new ProjectVideo { ProjectId = projectId, VideoId = videoId });
                db.Segments.Add(seg);
                await db.SaveChangesAsync(ct);
            }
            // 占位已落库 → 通知 UI 立刻显示 loading 占位卡。
            onSegmentCreated?.Invoke(segmentId);

            // 5) 整片 ASR（whisper）。失败 → text 留空、不阻断。
            onStage?.Invoke("识别中");
            var transcript = string.Empty;
            try
            {
                var tr = await _asr.TranscribeAsync(dest, videoDurationSec: video.Duration, cancellationToken: ct);
                transcript = tr.Text?.Trim() ?? string.Empty;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "[UserSegDiag] ASR 失败（text 留空）seg={Id}", segmentId); }

            // 6) 只打标不切分。无台词/无 key/失败 → 语义类型留默认「过渡」，可后补。
            onStage?.Invoke("打标中");
            List<SemanticType>? types = null;
            var position = PositionType.Middle;
            var keywords = new List<string>();
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                try
                {
                    var tags = await _ai.TagSingleSegmentAsync(transcript, cancellationToken: ct);
                    types = tags.EffectiveTypes.Select(ImportViewModel.NormalizeSemanticType).ToList();
                    position = NormalizePosition(tags.Position);
                    keywords = tags.Keywords;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger.LogWarning(ex, "[UserSegDiag] 打标失败（默认过渡）seg={Id}", segmentId); }
            }

            // 7) 写回结果 + 就绪。
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            {
                var s = await db.Segments.FirstOrDefaultAsync(x => x.Id == segmentId, ct);
                var v = await db.Videos.FirstOrDefaultAsync(x => x.Id == videoId, ct);
                if (s is not null)
                {
                    s.Text = transcript;
                    if (types is { Count: > 0 }) s.SemanticTypes = types;
                    s.PositionType = position;
                    if (keywords.Count > 0) s.Keywords = keywords;
                }
                if (v is not null)
                {
                    v.Transcript = transcript;
                    v.Status = VideoStatus.Completed;
                }
                await db.SaveChangesAsync(ct);
            }

            _logger.LogInformation("[UserSegDiag] 自建分镜就绪 seg={Id} dur={Dur:F1}s types={Types}",
                segmentId, dur, types is null ? "-" : string.Join("/", types));
            return new(name, UserSegmentImportStatus.Success, videoId, segmentId, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UserSegDiag] 上传自建分镜失败 file={File}", name);
            // 这条 Message 会被拼进「N 个跳过（xxx.mp4：…）」的 toast 直接给用户看，
            // 用裸 ex.Message 会漏出「The process cannot access the file...」这种英文原文。
            return new(name, UserSegmentImportStatus.Failed, null, null,
                MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex));
        }
    }

    /// <summary>ffprobe 读 width/height/fps/duration（与 ImportViewModel.ExtractMetadataAsync 同款）。</summary>
    private async Task ExtractMetadataAsync(Video video, CancellationToken ct)
    {
        var json = await _ffmpeg.RunProbeAsync(new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height,r_frame_rate",
            "-show_entries", "format=duration",
            "-of", "json", video.LocalPath,
        }, cancellationToken: ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durationEl)
            && double.TryParse(durationEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
        {
            video.Duration = duration;
        }
        if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
        {
            var stream = streams[0];
            if (stream.TryGetProperty("width", out var w)) video.Width = w.GetInt32();
            if (stream.TryGetProperty("height", out var h)) video.Height = h.GetInt32();
            if (stream.TryGetProperty("r_frame_rate", out var rEl) && rEl.GetString() is { } rate)
            {
                var parts = rate.Split('/');
                if (parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
                    && den > 0)
                {
                    video.Fps = num / den;
                }
            }
        }
    }

    /// <summary>位置归一化（与 ImportViewModel.NormalizePositionType 同款）。</summary>
    private static PositionType NormalizePosition(string raw)
    {
        var cleaned = raw.Trim();
        foreach (var p in PositionTypeExtensions.All)
        {
            if (p.ToLabel() == cleaned) return p;
        }
        var lower = cleaned.ToLowerInvariant();
        if (lower.Contains("开头") || lower.Contains("opening")) return PositionType.Opening;
        if (lower.Contains("结尾") || lower.Contains("ending")) return PositionType.Ending;
        return PositionType.Middle;
    }
}
