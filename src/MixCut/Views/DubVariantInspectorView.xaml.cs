using System;
using System.Windows;
using System.Windows.Controls;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>
/// 配音变体检视器（v0.5.0）。独立 UserControl，由宿主（MainWindow 右侧覆盖层）设置 DataContext =
/// DubVariantInspectorViewModel，并按 IsVisible 控制可视性。抽出是为了绕开 SegmentLibraryViewV2
/// content 区的水平测量怪象（右对齐内容会被推出屏幕外），改由 MainWindow 有界内容列右锚定渲染。
/// </summary>
public partial class DubVariantInspectorView : UserControl
{
    public DubVariantInspectorView()
    {
        InitializeComponent();
    }

    /// <summary>#15 打开逐句字幕编辑器（该变体已生成配音时）。</summary>
    private void OnOpenCaptionEditor(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DubVariantItemViewModel item) return;
        if (string.IsNullOrEmpty(item.AudioFilePath) || !System.IO.File.Exists(item.AudioFilePath))
        {
            Components.ToastService.Show("该变体还没有配音音频", Components.ToastStyle.Warning);
            return;
        }
        try
        {
            var win = new CaptionTimingEditorWindow(
                item.DubId, item.TextVariantIndex, item.CaptionSegmentDuration, item.AudioFilePath, item.Dubbing)
            {
                Owner = Window.GetWindow(this),
            };
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            Components.ToastService.Show("打开逐句字幕失败：" + ex.Message, Components.ToastStyle.Error);
        }
    }
}
