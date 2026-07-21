using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MixCut.Models;
using MixCut.Services.SchemeGeneration;
using MixCut.ViewModels;

namespace MixCut.Views.Schemes;

/// <summary>
/// 自定义叙事结构编辑器（issue #6 §四）：逐段选系统标签 → 实时候选数/预览名 → 生成方案。
/// 状态与渲染在 code-behind（对齐 SchemesView 动态构建风格）；生成走
/// <see cref="SchemeViewModel.CreateNarrativeStructureAsync"/>。
/// </summary>
public partial class NarrativeStructureEditorWindow : Window
{
    private readonly SchemeViewModel _vm;
    private readonly Project _project;
    private readonly IReadOnlyList<Segment> _segments;
    private readonly List<SemanticType> _availableTags;       // 只列库里真实有分镜的标签
    private readonly List<List<SemanticType>> _slots = new();  // 各段已选标签
    /// <summary>各段候选分镜的最短时长（秒），null=不限。与 <see cref="_slots"/> 同索引并行维护。对齐 macOS NarrativeSlot.minDuration。</summary>
    private readonly List<double?> _slotMin = new();
    /// <summary>各段候选分镜的最长时长（秒），null=不限。与 <see cref="_slots"/> 同索引并行维护。</summary>
    private readonly List<double?> _slotMax = new();
    private bool _generating;

    /// <summary>生成成功后非 null，供调用方刷新左栏并选中。</summary>
    public MixStrategy? CreatedStructure { get; private set; }

    public NarrativeStructureEditorWindow(SchemeViewModel vm, Project project, IReadOnlyList<Segment> segments)
    {
        _vm = vm;
        _project = project;
        _segments = segments;
        InitializeComponent();

        // 可选标签 = 库里真实出现过的语义类型（没素材的不出现，issue 核心原则①）
        _availableTags = SemanticTypeExtensions.All
            .Where(t => _segments.Any(s => s.SemanticTypes.Contains(t)))
            .ToList();

        AddSlot(); // 起始给一段空的
        RenderSlots();
    }

    /// <summary>新增一段（标签空 + 时长不限），保持三个并行列表同步。</summary>
    private void AddSlot()
    {
        _slots.Add(new List<SemanticType>());
        _slotMin.Add(null);
        _slotMax.Add(null);
    }

    /// <summary>删除一段，保持三个并行列表同步。</summary>
    private void RemoveSlot(int index)
    {
        _slots.RemoveAt(index);
        _slotMin.RemoveAt(index);
        _slotMax.RemoveAt(index);
    }

    private int VariationCount =>
        VariationCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out var n)
            ? n
            : 5;

    /// <summary>某段的候选数：标签 ∩ 分镜语义类型，再按该段时长区间过滤（与真实生成口径一致）。</summary>
    private int CandidateCount(int index)
    {
        var tags = _slots[index];
        if (tags.Count == 0) return 0;
        return NarrativeCandidatePool.CandidatesForSlot(
            _segments, new NarrativeSlot(0, tags.ToList(), _slotMin[index], _slotMax[index])).Count;
    }

    private void RenderSlots()
    {
        SlotsPanel.Children.Clear();

        if (_availableTags.Count == 0)
        {
            SlotsPanel.Children.Add(new TextBlock
            {
                Text = "当前项目还没有带语义标签的分镜，请先在分镜素材库分析视频。",
                FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 10, 4, 10),
            });
            GenerateButton.IsEnabled = false;
            PreviewNameText.Text = "（无可用标签）";
            return;
        }

        for (var i = 0; i < _slots.Count; i++)
        {
            SlotsPanel.Children.Add(BuildSlotRow(i));
        }

        // ＋ 添加一段
        var addBtn = new Button
        {
            Content = "＋ 添加一段",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5)),
            Cursor = Cursors.Hand,
        };
        addBtn.Click += (_, _) => { AddSlot(); RenderSlots(); };
        SlotsPanel.Children.Add(addBtn);

        UpdatePreviewAndGate();
    }

    private UIElement BuildSlotRow(int index)
    {
        var tags = _slots[index];
        var cand = CandidateCount(index);
        var hasError = tags.Count == 0 || cand == 0;

        var border = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            BorderBrush = hasError
                ? new SolidColorBrush(Color.FromRgb(0xD3, 0x3A, 0x3A))
                : new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE6)),
            BorderThickness = new Thickness(hasError ? 1.5 : 1),
        };

        var root = new StackPanel();

        // 第一行：序号 + 候选数 + 上移/下移/删除
        var top = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        var numText = new TextBlock
        {
            Text = $"第 {index + 1} 段", FontSize = 12, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(numText, Dock.Left);
        top.Children.Add(numText);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);

        var candText = new TextBlock
        {
            Text = tags.Count == 0
                ? "未选标签"
                : (cand > 30 ? $"候选 30（共 {cand}，按质量取前 30）" : $"候选 {cand}"),
            FontSize = 11,
            Foreground = hasError
                ? new SolidColorBrush(Color.FromRgb(0xD3, 0x3A, 0x3A))
                : Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        actions.Children.Add(candText);

        if (index > 0)
        {
            actions.Children.Add(MiniButton("▲", () => { Swap(index, index - 1); }));
        }
        if (index < _slots.Count - 1)
        {
            actions.Children.Add(MiniButton("▼", () => { Swap(index, index + 1); }));
        }
        if (_slots.Count > 1)
        {
            actions.Children.Add(MiniButton("🗑", () => { RemoveSlot(index); RenderSlots(); }));
        }
        top.Children.Add(actions);
        root.Children.Add(top);

        // 第二行：标签 chips + ＋加标签
        var wrap = new WrapPanel();
        foreach (var tag in tags)
        {
            wrap.Children.Add(BuildTagChip(index, tag));
        }
        wrap.Children.Add(BuildAddTagButton(index));
        root.Children.Add(wrap);

        // 第三行：时长区间过滤（留空 = 不限）。对齐 macOS「时长 [不限] ~ [不限] 秒」+ lo>hi 校验提示。
        // 例：设 5 ~ 8，则该段只从「时长 5~8 秒」的分镜里选片（候选数会实时跟着变）。
        var durRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        durRow.Children.Add(new TextBlock
        {
            Text = "时长", FontSize = 11, Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        });
        durRow.Children.Add(DurationBox(_slotMin[index], v => { _slotMin[index] = v; RenderSlots(); }));
        durRow.Children.Add(new TextBlock
        {
            Text = "~", FontSize = 11, Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0),
        });
        durRow.Children.Add(DurationBox(_slotMax[index], v => { _slotMax[index] = v; RenderSlots(); }));
        durRow.Children.Add(new TextBlock
        {
            Text = "秒", FontSize = 11, Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        });
        if (_slotMin[index] is { } lo && _slotMax[index] is { } hi && lo > hi)
        {
            durRow.Children.Add(new TextBlock
            {
                Text = "⚠ 最短时长不能大于最长", FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD3, 0x3A, 0x3A)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            });
        }
        root.Children.Add(durRow);

        border.Child = root;
        return border;
    }

    /// <summary>
    /// 时长输入框（留空 = 不限）。失焦 / 回车提交；非法输入（负数、非数字）忽略并保留原值，
    /// 对齐 macOS durationBinding 的「空串↔nil、非法输入保留原值」语义。空文本时显示灰色「不限」占位。
    /// </summary>
    private UIElement DurationBox(double? value, Action<double?> onCommit)
    {
        var box = new TextBox
        {
            Text = value is { } v ? v.ToString("0.#", CultureInfo.InvariantCulture) : string.Empty,
            FontSize = 11, Padding = new Thickness(4, 2, 4, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "留空 = 不限",
        };
        var placeholder = new TextBlock
        {
            Text = "不限", FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed,
        };
        box.TextChanged += (_, _) =>
            placeholder.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;

        void Commit()
        {
            var t = box.Text.Trim();
            if (t.Length == 0)
            {
                onCommit(null);   // 空 = 不限
                return;
            }
            onCommit(double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0
                ? d
                : value);         // 非法输入 → 保留原值
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };

        var grid = new Grid { Width = 58 };
        grid.Children.Add(box);
        grid.Children.Add(placeholder);
        return grid;
    }

    private UIElement BuildTagChip(int slotIndex, SemanticType tag)
    {
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(tag.ToColorHex()); }
        catch { color = Color.FromRgb(0x1D, 0x6B, 0xE5); }

        var chip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 6, 3),
            Margin = new Thickness(0, 0, 6, 6),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = tag.ToLabel(), FontSize = 11,
            Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center,
        });
        var del = new TextBlock
        {
            Text = "✕", FontSize = 10, Margin = new Thickness(5, 0, 0, 0),
            Foreground = new SolidColorBrush(color), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        del.MouseLeftButtonUp += (_, _) => { _slots[slotIndex].Remove(tag); RenderSlots(); };
        sp.Children.Add(del);
        chip.Child = sp;
        return chip;
    }

    private UIElement BuildAddTagButton(int slotIndex)
    {
        var btn = new Button
        {
            Content = "＋加标签", FontSize = 11, Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xD0)),
            BorderThickness = new Thickness(1),
        };
        var menu = new ContextMenu();
        var remaining = _availableTags.Where(t => !_slots[slotIndex].Contains(t)).ToList();
        if (remaining.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "（已全部添加）", IsEnabled = false });
        }
        foreach (var tag in remaining)
        {
            var item = new MenuItem { Header = tag.ToLabel() };
            var captured = tag;
            item.Click += (_, _) => { _slots[slotIndex].Add(captured); RenderSlots(); };
            menu.Items.Add(item);
        }
        btn.Click += (_, _) =>
        {
            menu.PlacementTarget = btn;
            menu.IsOpen = true;
        };
        return btn;
    }

    private Button MiniButton(string glyph, Action onClick)
    {
        var b = new Button
        {
            Content = glyph, Width = 26, Height = 24, FontSize = 11, Margin = new Thickness(2, 0, 0, 0),
            Background = Brushes.Transparent, BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xE0)),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Swap(int a, int b)
    {
        (_slots[a], _slots[b]) = (_slots[b], _slots[a]);
        (_slotMin[a], _slotMin[b]) = (_slotMin[b], _slotMin[a]);   // 时长区间跟着段一起换位
        (_slotMax[a], _slotMax[b]) = (_slotMax[b], _slotMax[a]);
        RenderSlots();
    }

    private void UpdatePreviewAndGate()
    {
        var preview = string.Join(" · ", _slots
            .Where(s => s.Count > 0)
            .Select(s => string.Join("/", s.Select(t => t.ToLabel()))));
        PreviewNameText.Text = string.IsNullOrEmpty(preview) ? "（先给每段添加标签）" : preview;

        var allValid = _slots.Count > 0 && _slots.Select((s, i) => (s, i)).All(x => x.s.Count > 0 && CandidateCount(x.i) > 0);
        GenerateButton.IsEnabled = allValid && !_generating;
    }

    private async void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        if (_generating)
        {
            return;
        }
        try
        {
            _generating = true;
            GenerateButton.IsEnabled = false;
            StatusText.Text = "正在生成方案（AI 选片 + 台词连贯校验）…";

            var slots = _slots
                .Select((tags, i) => new NarrativeSlot(i + 1, tags.ToList(), _slotMin[i], _slotMax[i]))
                .ToList();

            var result = await _vm.CreateNarrativeStructureAsync(_project, slots, _segments, VariationCount);

            if (result is not null)
            {
                CreatedStructure = result;
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = "未生成连贯变体，建议调整段位标签或增加素材";
                _generating = false;
                UpdatePreviewAndGate();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[NarrativeGen] 编辑器生成异常");
            // §红线：不给用户裸 ex.Message —— 翻成人话（AI 异常本就中文，其它走兜底）。
            StatusText.Text = "生成失败：" + MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex);
            _generating = false;
            UpdatePreviewAndGate();
        }
    }
}
