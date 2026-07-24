using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MixCut.Services.Bgm;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Views;

/// <summary>
/// 全局 BGM 库页面（issue #22，对应 mac BGMLibraryView）：上传（多选）/ 列表（文件名 + 时长 + 试听）/
/// 删除（带确认）/ 空态引导。全局共享（所有项目可见可用），不实现 IProjectView。
/// </summary>
public partial class BgmLibraryView : UserControl
{
    private readonly BgmLibraryService _library;
    private readonly FFmpegRunner _ffmpeg;

    // 试听：与 DubbingViewModel 同款 NAudio 管道 —— mp3/m4a 先用自带 ffmpeg 解成 PCM wav 再播
    // （AudioFileReader 对 mp3/m4a 走 MediaFoundation，N 版 Windows 没有 —— §兼容性总纲：不碰系统 codec）。
    private NAudio.Wave.IWavePlayer? _waveOut;
    private NAudio.Wave.AudioFileReader? _audioReader;
    private string? _playingPath;
    private bool _uploading;

    public BgmLibraryView(BgmLibraryService library, FFmpegRunner ffmpeg)
    {
        _library = library;
        _ffmpeg = ffmpeg;
        InitializeComponent();
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true) await RefreshAsync();
            else StopPlayback(); // 离开页面停掉试听，别让声音跟到别的视图
        };
        _ = RefreshAsync();
    }

    // ---- 列表 ----

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        try
        {
            var items = await _library.ListAsync();
            ListPanel.Children.Clear();
            EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ListScroll.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var item in items)
            {
                ListPanel.Children.Add(BuildRow(item));
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[BgmDiag] 刷新 BGM 库列表失败");
        }
    }

    private UIElement BuildRow(BgmItem item)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE7)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = "🎵", FontSize = 16, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var name = new TextBlock
        {
            Text = item.FileName, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var duration = new TextBlock
        {
            Text = FrameTime.HumanDuration(item.DurationSeconds), FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
        };
        Grid.SetColumn(duration, 2);
        grid.Children.Add(duration);

        var playBtn = new Button
        {
            Content = _playingPath == item.FullPath ? "⏸ 暂停" : "▶ 试听",
            Padding = new Thickness(10, 4, 10, 4), FontSize = 11, Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 0),
        };
        playBtn.Click += async (_, _) => await TogglePlayAsync(item.FullPath);
        Grid.SetColumn(playBtn, 3);
        grid.Children.Add(playBtn);

        var deleteBtn = new Button
        {
            Content = "删除",
            Padding = new Thickness(10, 4, 10, 4), FontSize = 11, Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.White,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD3, 0x3A, 0x3A)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xB7, 0xB7)),
            BorderThickness = new Thickness(1),
        };
        deleteBtn.Click += async (_, _) => await DeleteAsync(item);
        Grid.SetColumn(deleteBtn, 4);
        grid.Children.Add(deleteBtn);

        border.Child = grid;
        return border;
    }

    // ---- 上传 ----

    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_uploading) return;
            var exts = string.Join(";", BgmLibraryService.AudioExtensions.Select(x => "*" + x));
            var dialog = new OpenFileDialog
            {
                Title = "选择要上传的音乐",
                Multiselect = true,
                Filter = $"音频文件|{exts}|所有文件|*.*",
            };
            if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;

            _uploading = true;
            UploadButton.IsEnabled = false;
            UploadButton.Content = "上传中…";
            try
            {
                var result = await _library.UploadAsync(dialog.FileNames);
                await RefreshAsync();

                // Toast：全部成功报条数；有失败必须逐条列出文件名 + 人话原因（issue #22 §3.1）。
                if (result.Failures.Count == 0)
                {
                    Components.ToastService.Show(
                        $"✓ 已上传 {result.SuccessCount} 首音乐", Components.ToastStyle.Success);
                }
                else
                {
                    var detail = string.Join("\n", result.Failures.Select(f => $"• {f.FileName}：{f.Reason}"));
                    var head = result.SuccessCount > 0
                        ? $"上传完成 {result.SuccessCount} 首，{result.Failures.Count} 首失败：\n"
                        : "上传失败：\n";
                    Shared.MixCutDialog.Error(Window.GetWindow(this), "部分文件未能上传", head + detail);
                }
            }
            finally
            {
                _uploading = false;
                UploadButton.IsEnabled = true;
                UploadButton.Content = "⬆ 上传音乐";
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[BgmDiag] 上传流程异常");
            Components.ToastService.Show("上传没能完成，请重试", Components.ToastStyle.Error);
        }
    }

    // ---- 删除 ----

    private async System.Threading.Tasks.Task DeleteAsync(BgmItem item)
    {
        try
        {
            var confirmed = Shared.MixCutDialog.Confirm(
                Window.GetWindow(this),
                $"删除「{item.FileName}」？",
                "删除后无法恢复。已导出的视频不受影响。",
                confirmText: "删除", cancelText: "取消", destructive: true, icon: "🗑");
            if (!confirmed) return;

            if (_playingPath == item.FullPath) StopPlayback();
            if (_library.Delete(item.FullPath))
            {
                Components.ToastService.Show($"已删除「{item.FileName}」", Components.ToastStyle.Success);
            }
            else
            {
                Components.ToastService.Show("删除失败，文件可能正被占用，请稍后重试", Components.ToastStyle.Error);
            }
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[BgmDiag] 删除流程异常");
        }
    }

    // ---- 试听 ----

    private async System.Threading.Tasks.Task TogglePlayAsync(string path)
    {
        try
        {
            if (_playingPath == path)
            {
                StopPlayback();
                await RefreshAsync();   // 刷新按钮文案 ▶/⏸
                return;
            }
            StopPlayback();

            // mp3/m4a → 自带 ffmpeg 解成临时 PCM wav（按源文件名+修改时间缓存，重复试听秒开）。
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;
            var wav = Path.Combine(Path.GetTempPath(),
                $"mixcut-bgm-preview-{Path.GetFileNameWithoutExtension(path)}-{stamp}.wav");
            if (!File.Exists(wav))
            {
                await _ffmpeg.RunAsync(
                    new[] { "-y", "-i", path, "-vn", "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le", wav },
                    timeout: TimeSpan.FromMinutes(2));
            }

            _audioReader = new NAudio.Wave.AudioFileReader(wav);
            _waveOut = new NAudio.Wave.WaveOutEvent();
            _waveOut.Init(_audioReader);
            _playingPath = path;
            _waveOut.PlaybackStopped += (_, _) => Dispatcher.Invoke(async () =>
            {
                if (_playingPath == path)
                {
                    StopPlayback();
                    await RefreshAsync();
                }
            });
            _waveOut.Play();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StopPlayback();
            Serilog.Log.Warning(ex, "[BgmDiag] 试听失败 path={Path}", path);
            Components.ToastService.Show("试听失败，文件可能已损坏或被占用", Components.ToastStyle.Error);
        }
    }

    private void StopPlayback()
    {
        try { _waveOut?.Stop(); } catch { /* 尽力 */ }
        try { _waveOut?.Dispose(); } catch { /* 尽力 */ }
        try { _audioReader?.Dispose(); } catch { /* 尽力 */ }
        _waveOut = null;
        _audioReader = null;
        _playingPath = null;
    }
}
