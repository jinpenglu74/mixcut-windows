using System.Windows;

namespace MixCut.Views.Shared;

/// <summary>
/// 全局统一的自绘对话框，替代原生 <see cref="MessageBox"/>。
///
/// 为什么必须换掉 MessageBox：它是产品里唯一没有设计的界面 —— Win32 灰底、系统字体渲染、
/// 直角、和应用无关的系统关闭按钮、系统提示音 —— 而它恰好出现在最关键的决策时刻
/// （删除数据、覆盖文件、确认计费）。用户对「这软件靠不靠谱」的判断就在这一秒完成。
///
/// 三条破坏性确认铁律（Apple HIG，原生 MessageBox 一条都做不到）：
///   ① 默认按钮必须是安全的那个（危险操作时焦点落在「取消」，防止习惯性回车误删）
///   ② 按钮文案是动词而非「确定」（"删除 12 个分镜" 比 "确定" 自解释）
///   ③ 视觉必须属于本应用
/// </summary>
public partial class MixCutDialog : Window
{
    private MixCutDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 确认对话框。<paramref name="destructive"/>=true 时主按钮转红、且默认焦点落在「取消」。
    /// </summary>
    /// <param name="confirmText">主操作文案，用动词（如「删除项目」「覆盖」「生成并计费」）。</param>
    public static bool Confirm(
        Window? owner, string title, string message,
        string confirmText = "确定", string cancelText = "取消",
        bool destructive = false, string? icon = null)
    {
        var dlg = Create(owner, title, message, icon, destructive ? BadgeTone.Danger : BadgeTone.Normal);
        dlg.ConfirmButton.Content = confirmText;
        dlg.CancelButton.Content = cancelText;
        dlg.CancelButton.Visibility = Visibility.Visible;

        if (destructive)
        {
            // 危险操作：主按钮红色 + 不设为默认键 + 焦点给「取消」
            if (dlg.TryFindResource("DangerButtonStyle") is Style danger)
            {
                dlg.ConfirmButton.Style = danger;
            }
            dlg.Loaded += (_, _) => dlg.CancelButton.Focus();
        }
        else
        {
            dlg.ConfirmButton.IsDefault = true;
            dlg.Loaded += (_, _) => dlg.ConfirmButton.Focus();
        }

        return dlg.ShowDialog() == true;
    }

    /// <summary>图标圆底的语义色。</summary>
    private enum BadgeTone { Normal, Danger }

    /// <summary>单按钮提示（仅告知，无选择）。</summary>
    public static void Alert(Window? owner, string title, string message, string okText = "知道了", string? icon = null)
        => Alert(owner, title, message, okText, icon, BadgeTone.Normal);

    private static void Alert(Window? owner, string title, string message, string okText, string? icon, BadgeTone tone)
    {
        var dlg = Create(owner, title, message, icon, tone);
        dlg.ConfirmButton.Content = okText;
        dlg.ConfirmButton.IsDefault = true;
        dlg.CancelButton.Visibility = Visibility.Collapsed;
        dlg.Loaded += (_, _) => dlg.ConfirmButton.Focus();
        dlg.ShowDialog();
    }

    /// <summary>错误提示（红色感叹号图标）。文案必须是人话，不许出现 stack trace / 错误码。</summary>
    public static void Error(Window? owner, string title, string message) =>
        Alert(owner, title, message, "知道了", "⚠", BadgeTone.Danger);

    private static MixCutDialog Create(Window? owner, string title, string message, string? icon,
        BadgeTone tone = BadgeTone.Normal)
    {
        var dlg = new MixCutDialog
        {
            TitleText = { Text = title },
            MessageText = { Text = message },
        };

        // Owner 必须在构造之后单独赋值，且要挡住「把自己设成自己的 Owner」：
        // WPF 会把进程里第一个创建的 Window 自动登记为 Application.Current.MainWindow，
        // 所以当主窗还不存在时（启动早期、主窗已关闭、调用方传了 null），
        // Application.Current.MainWindow 拿到的就是这个对话框本身 → 赋值直接抛
        // ArgumentException「无法将 Owner 属性设置为它本身」，整个应用崩掉。
        var target = owner ?? Application.Current?.MainWindow;
        if (target is not null && !ReferenceEquals(target, dlg))
        {
            dlg.Owner = target;
        }
        else
        {
            // 没有可用 Owner 时 CenterOwner 会退化成屏幕左上角，兜底居中屏幕。
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (!string.IsNullOrEmpty(icon))
        {
            dlg.IconText.Text = icon;
            dlg.IconBadge.Visibility = Visibility.Visible;
            var brushKey = tone == BadgeTone.Danger ? "DangerRedBgBrush" : "AccentBlueLightBrush";
            if (dlg.TryFindResource(brushKey) is System.Windows.Media.Brush b)
            {
                dlg.IconBadge.Background = b;
            }
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            dlg.MessageText.Visibility = Visibility.Collapsed;
        }
        return dlg;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
