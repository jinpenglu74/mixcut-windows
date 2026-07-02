namespace MixCut.Models;

/// <summary>
/// 单分镜配音（<see cref="SegmentDub"/>）的状态。对应 macOS DubEnums.SegmentDubStatus。
/// </summary>
public enum SegmentDubStatus
{
    /// <summary>待生成音频。</summary>
    Pending,
    /// <summary>已生成音频。</summary>
    Generated,
    /// <summary>生成失败（可单独重生成）。</summary>
    Failed,
}

/// <summary>
/// 硬字幕遮挡底层样式（存 <see cref="Segment.MaskStyleRaw"/>）。对应 macOS DubEnums.MaskStyle。
/// 注意：v0.5.0 已砍掉「半透明 dim」，旧数据归入 <see cref="Blur"/>。
/// </summary>
public enum MaskStyle
{
    /// <summary>高斯模糊。</summary>
    Blur,
    /// <summary>纯色遮挡。</summary>
    Solid,
}

/// <summary>
/// 烧录字幕字号（全局统一）。对应 macOS DubEnums.SubtitleFontSize。
/// 以「相对成片宽度的连续比例」存储（存 <see cref="MixCut.Utilities.AppSettings.SubtitleFontRatio"/>），
/// 导出时按分辨率换算像素 —— 任意分辨率下观感一致（根治小分辨率素材上字幕巨大的问题）。
/// 参考实拍原字幕≈画面宽 7%，故范围取 3%~8.5%，默认 5.5%。
/// 说明：与 Mac 不同，这里做成「纯」静态工具（比例由调用方从 AppSettings 读入后传参），
/// 不在类内部读全局单例——避免静态工具耦合 DI 单例，同时导出路径与 UI 预览共用同一个 AppSettings 值。
/// </summary>
public static class SubtitleFontSize
{
    /// <summary>最小比例（很小）。</summary>
    public const double MinRatio = 0.030;
    /// <summary>最大比例（匹配/略超大号原字幕）。</summary>
    public const double MaxRatio = 0.085;
    /// <summary>默认比例。</summary>
    public const double DefaultRatio = 0.055;

    /// <summary>夹取到合法区间。</summary>
    public static double Clamp(double ratio) => Math.Min(MaxRatio, Math.Max(MinRatio, ratio));

    /// <summary>按成片宽度换算字号像素（下限 12px 兜底极小分辨率）。对齐 mac fontSize(forOutputWidth:)。</summary>
    public static double FontSize(int outputWidth, double ratio) => Math.Max(12.0, outputWidth * Clamp(ratio));
}

/// <summary>
/// 分镜「字幕处理」方式（UI 三选一，不直接入库）。对应 macOS DubEnums.SubtitleTreatment。
/// 映射到底层 (<see cref="Segment.HasHardSubtitle"/>, <see cref="Segment.MaskStyleRaw"/>) 两字段，避免数据库迁移。
/// </summary>
public enum SubtitleTreatment
{
    /// <summary>直接烧录：不处理背景，直接把新字幕烧上去。</summary>
    Direct,
    /// <summary>模糊虚化：把字幕区域糊掉再烧（不管原片有没有旧字幕都可用）。</summary>
    Blur,
    /// <summary>纯色遮挡：深色条盖住该区域再烧。</summary>
    Solid,
}

/// <summary>
/// 字幕遮挡框的归一化坐标（0..1，相对输出画面）。对应 macOS SubtitleMaskRect。
/// 存 <see cref="Segment.MaskRectJson"/>，导出时按输出像素换算。
/// </summary>
public readonly record struct SubtitleMaskRect(double X, double Y, double Width, double Height)
{
    /// <summary>默认遮挡框：宽度顶满视频、底部偏下的一条（与剪映字幕位置接近）。</summary>
    public static SubtitleMaskRect Default => new(0.0, 0.78, 1.0, 0.12);

    /// <summary>最小尺寸，避免拖塌缩成 0。对齐 mac SubtitleMaskRect.minSize。</summary>
    private const double MinSize = 0.02;

    /// <summary>钳制进 [0,1] 画面，保证不出界、不塌缩。对齐 mac clamped()。</summary>
    public SubtitleMaskRect Clamped()
    {
        var w = Math.Min(Math.Max(Width, MinSize), 1.0);
        var h = Math.Min(Math.Max(Height, MinSize), 1.0);
        var cx = Math.Min(Math.Max(X, 0.0), 1.0 - w);
        var cy = Math.Min(Math.Max(Y, 0.0), 1.0 - h);
        return new SubtitleMaskRect(cx, cy, w, h);
    }

    /// <summary>上下平移后钳制（只动 Y，对齐 mac movedBy）。</summary>
    public SubtitleMaskRect MovedBy(double dy) => new SubtitleMaskRect(X, Y + dy, Width, Height).Clamped();

    /// <summary>改高度后钳制（底边不出界，对齐 mac resizedBy）。</summary>
    public SubtitleMaskRect ResizedBy(double dHeight) => new SubtitleMaskRect(X, Y, Width, Height + dHeight).Clamped();
}

/// <summary>
/// <see cref="MaskStyle"/> / <see cref="SubtitleTreatment"/> 与底层字段互转。
/// </summary>
public static class SubtitleTreatmentExtensions
{
    public static MaskStyle ParseMaskStyle(string? raw) =>
        string.Equals(raw, nameof(MaskStyle.Solid), StringComparison.OrdinalIgnoreCase)
            ? MaskStyle.Solid
            : MaskStyle.Blur;

    /// <summary>是否需要遮挡框（blur/solid 需要定位区域；direct 不需要）。</summary>
    public static bool NeedsMask(this SubtitleTreatment t) => t != SubtitleTreatment.Direct;

    /// <summary>从底层字段还原。旧「半透明 dim」统一归入「模糊虚化」。</summary>
    public static SubtitleTreatment FromFields(bool hasHardSubtitle, string? maskStyleRaw)
    {
        if (!hasHardSubtitle) return SubtitleTreatment.Direct;
        return ParseMaskStyle(maskStyleRaw) == MaskStyle.Solid
            ? SubtitleTreatment.Solid
            : SubtitleTreatment.Blur;
    }

    /// <summary>映射回底层 (hasHardSubtitle, maskStyleRaw)。</summary>
    public static (bool HasHardSubtitle, string MaskStyleRaw) ToFields(this SubtitleTreatment t) => t switch
    {
        SubtitleTreatment.Direct => (false, nameof(MaskStyle.Blur)),
        SubtitleTreatment.Solid => (true, nameof(MaskStyle.Solid)),
        _ => (true, nameof(MaskStyle.Blur)),
    };
}
