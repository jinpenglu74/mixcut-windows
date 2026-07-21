using System.Windows;
using System.Windows.Media;

namespace MixCut.Infrastructure;

/// <summary>
/// code-behind 访问设计系统 token 的统一入口。
///
/// 为什么需要它：本项目有数千行动态构建 UI 的 code-behind（SchemesView / SegmentLibraryView /
/// ShotEditWindow / SettingsWindow …）。XAML 里可以写 <c>{StaticResource AccentBlueBrush}</c>，
/// 但 C# 里没有等价的低摩擦写法，于是全都退化成 <c>new SolidColorBrush(Color.FromRgb(0x1D,0x6B,0xE5))</c>
/// —— 仅 <c>Color.FromRgb</c> 就有 187 处，其中 70% 的色值与既有 token 完全相同。
/// 「用 token」比「写死」更麻烦，规范就一定会被绕过；这个类就是把摩擦力降下来。
///
/// 用法：<c>Foreground = Theme.TextSecondary</c>、<c>Background = Theme.Brush("SemHookBrush")</c>、
/// <c>FontSize = Theme.FontSize.Body</c>。
///
/// 找不到 key 时返回安全兜底值而不是抛异常 —— 动态 UI 构建往往在渲染路径上，
/// 一个拼错的 key 不该让整个界面崩掉（对齐 CLAUDE.md：StaticResource 解析失败会崩窗口的教训）。
/// </summary>
public static class Theme
{
    private static object? Find(string key)
    {
        try { return Application.Current?.TryFindResource(key); }
        catch { return null; }
    }

    /// <summary>按 key 取画刷；找不到时回退透明（不抛异常，不让渲染路径崩）。</summary>
    public static Brush Brush(string key) => Find(key) as Brush ?? Brushes.Transparent;

    /// <summary>按 key 取颜色值（用于动画/渐变）。</summary>
    public static Color Color(string key) =>
        Find(key) switch
        {
            Color c => c,
            SolidColorBrush b => b.Color,
            _ => Colors.Transparent,
        };

    // ---- 品牌色 ----
    public static Brush Accent => Brush("AccentBlueBrush");
    public static Brush AccentDark => Brush("AccentBlueDarkBrush");
    public static Brush AccentLight => Brush("AccentBlueLightBrush");
    public static Brush AccentHover => Brush("AccentBlueHoverBrush");
    public static Brush Purple => Brush("PurpleAccentBrush");

    // ---- 语义色 ----
    public static Brush Success => Brush("SuccessGreenBrush");
    public static Brush Warning => Brush("WarningOrangeBrush");
    public static Brush Danger => Brush("DangerRedBrush");

    // ---- 文本色阶（从深到浅）----
    public static Brush TextPrimary => Brush("TextPrimaryBrush");
    public static Brush TextSecondary => Brush("TextSecondaryBrush");
    public static Brush TextTertiary => Brush("TextTertiaryBrush");
    public static Brush TextQuaternary => Brush("TextQuaternaryBrush");
    public static Brush TextQuinary => Brush("TextQuinaryBrush");
    public static Brush TextDisabled => Brush("TextDisabledBrush");
    public static Brush TextOnAccent => Brush("TextOnAccentBrush");

    // ---- 背景层 ----
    public static Brush BgPrimary => Brush("BgPrimaryBrush");
    public static Brush BgSecondary => Brush("BgSecondaryBrush");
    public static Brush BgTertiary => Brush("BgTertiaryBrush");
    public static Brush BgSubtle => Brush("BgSubtleBrush");
    public static Brush BgHover => Brush("BgHoverBrush");

    // ---- 边框 ----
    public static Brush BorderPrimary => Brush("BorderPrimaryBrush");
    public static Brush BorderSubtle => Brush("BorderSubtleBrush");
    public static Brush BorderStrong => Brush("BorderStrongBrush");
    public static Brush BorderHover => Brush("BorderHoverBrush");

    // ---- 深色遮罩（叠在视频画面上）----
    public static Brush OverlaySubtle => Brush("OverlayDarkSubtleBrush");
    public static Brush OverlayMedium => Brush("OverlayDarkMediumBrush");
    public static Brush OverlayStrong => Brush("OverlayDarkStrongBrush");
    public static Brush OverlayVeryStrong => Brush("OverlayDarkVeryStrongBrush");

    /// <summary>字号档位（与 Typography.xaml 同源，禁止在 code-behind 里写字面量字号）。</summary>
    public static class FontSize
    {
        private static double Get(string key, double fallback) => Find(key) is double d ? d : fallback;

        /// <summary>10 —— 徽章、角标等极辅助信息。</summary>
        public static double Caption => Get("FontSizeCaption", 10);
        /// <summary>11 —— 次要说明文字（中文可读下限）。</summary>
        public static double BodySmall => Get("FontSizeBodySmall", 11);
        /// <summary>12 —— 正文默认。</summary>
        public static double Body => Get("FontSizeBody", 12);
        /// <summary>13 —— 强调正文 / 台词。</summary>
        public static double BodyLarge => Get("FontSizeBodyLarge", 13);
        /// <summary>14 —— 小标题。</summary>
        public static double Subtitle => Get("FontSizeSubtitle", 14);
        /// <summary>16 —— 区块标题 / 弹窗标题。</summary>
        public static double H3 => Get("FontSizeH3", 16);
        /// <summary>22 —— 页面主标题。</summary>
        public static double H1 => Get("FontSizeH1", 22);
    }

    /// <summary>等宽字体（时间码、帧号、数字对齐场景）。</summary>
    public static FontFamily Mono =>
        Find("FontFamilyMono") as FontFamily ?? new FontFamily("Consolas");
}
