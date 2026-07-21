using System.Globalization;
using System.Windows.Data;

namespace MixCut.Views.Converters;

/// <summary>
/// ItemsControl.AlternationIndex（0 起）→ 展示用序号（1 起）。
/// 「调整顺序」弹窗用它在每张分镜卡上标「1 / 2 / 3」，让当前先后顺序一眼可见。
/// </summary>
public sealed class IndexToOrderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? (i + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
