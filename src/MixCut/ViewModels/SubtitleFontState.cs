using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MixCut.Models;
using MixCut.Utilities;

namespace MixCut.ViewModels;

/// <summary>
/// 烧录字幕字号（全局比例）的共享可观察状态。对齐 mac 的 @AppStorage("subtitleFontRatio") ——
/// 一个滑条控制全片，所有分镜卡片的滑条与「所见即所得」预览共用同一份值，任一处改动实时联动。
///
/// 为什么用单例：字号是<b>全局</b>设置（非 per-segment），若挂在每个 <see cref="Cards.SegmentCardViewModel"/>
/// 上就无法跨卡片联动。这里做成进程级单例，Slider 双向绑定它、预览 overlay 订阅 <see cref="RatioChanged"/>，
/// 落盘走注入的 <see cref="AppSettings"/>（启动时 <see cref="Attach"/> 一次），与导出烧录读同一个键。
/// </summary>
public sealed partial class SubtitleFontState : ObservableObject
{
    public static SubtitleFontState Shared { get; } = new();

    private Action<double>? _persist;

    private SubtitleFontState()
    {
        _ratio = SubtitleFontSize.DefaultRatio;
    }

    /// <summary>启动时用 DI 的 <see cref="AppSettings"/> 初始化：读初值 + 提供落盘回调。仅调一次。</summary>
    public void Attach(AppSettings settings)
    {
        _persist = r => settings.SubtitleFontRatio = r;
        // 走属性 setter：回写同一个值幂等无副作用，同时触发 PropertyChanged + RatioChanged 让绑定/预览初始化。
        Ratio = settings.SubtitleFontRatio;
    }

    /// <summary>当前全局字号比例（相对成片宽度）。Slider 双向绑定。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private double _ratio;

    partial void OnRatioChanged(double value)
    {
        var clamped = SubtitleFontSize.Clamp(value);
        _persist?.Invoke(clamped);
        RatioChanged?.Invoke(clamped);
    }

    /// <summary>比例变化事件（供非绑定端如预览 overlay 的 code-behind 订阅刷新）。</summary>
    public event Action<double>? RatioChanged;

    /// <summary>"5.5%" 显示文本。</summary>
    public string PercentText =>
        (SubtitleFontSize.Clamp(Ratio) * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
}
