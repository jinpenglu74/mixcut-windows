using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using MixCut.Models;

namespace MixCut.Services.Dubbing;

/// <summary>渲染好的字幕 PNG 信息。</summary>
public sealed record CaptionImage(string Path, int PixelWidth, int PixelHeight);

/// <summary>
/// 字幕渲染（v0.5.0，libass-free）：用 GDI+ 把台词渲染成透明 PNG，再由 ffmpeg overlay 叠到画面。
/// 对应 mac「系统文字渲染成透明 PNG」。<b>按输出像素渲染</b>（不经 DPI 缩放），避免高分屏字糊/错位。
/// </summary>
public static class CaptionRenderer
{
    private const string FontFamily = "Microsoft YaHei"; // 微软雅黑，Win10/11 自带，中文清晰

    /// <summary>恒定转空格的标点（中文标点、括号、引号、感叹问号等）。对齐 mac stripPunctuation alwaysPunct。</summary>
    private static readonly HashSet<char> AlwaysPunct = new()
    {
        '，', '。', '、', '！', '？', '；', '：', '“', '”', '‘', '’',
        '「', '」', '『', '』', '（', '）', '【', '】', '《', '》', '〈', '〉',
        '…', '—', '～', '·', '・', '　',
        '!', '?', ';', '"', '\'', '(', ')', '[', ']', '{', '}', '<', '>', '~',
    };

    /// <summary>
    /// 烧录字幕预处理：中英文标点转空格并合并多余空白（短视频字幕风格，既符合观感也利于换行）。
    /// 半角 <c>. , :</c> 是数字内部分隔符（9.9元 / 1,000 / 8:00）——仅前后都是 ASCII 数字时保留，
    /// 否则视为标点转空格，避免破坏广告里的价格/折扣/时间数字卖点。对齐 mac CaptionRenderer.stripPunctuation。
    /// </summary>
    public static string StripPunctuation(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var chars = text.ToCharArray();
        bool IsAsciiDigit(int i) => i >= 0 && i < chars.Length && chars[i] >= '0' && chars[i] <= '9';

        var sb = new System.Text.StringBuilder(chars.Length);
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (AlwaysPunct.Contains(c))
                sb.Append(' ');
            else if (c is '.' or ',' or ':')
                sb.Append(IsAsciiDigit(i - 1) && IsAsciiDigit(i + 1) ? c : ' ');
            else
                sb.Append(c);
        }
        // 合并连续空白为单个空格并去首尾
        return string.Join(' ', sb.ToString().Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// 把字幕渲染到 PNG。<paramref name="canvasWidth"/> = 字幕区宽（=遮挡区宽，字幕在其内换行）。
    /// <paramref name="fontSize"/> = 字号像素（由导出端按 <see cref="SubtitleFontSize.FontSize(int,double)"/> 算好传入）。
    /// <paramref name="withBackdrop"/>=true 时加贴字圆角胶囊底衬（纯色遮挡模式已有底，传 false）。
    /// 返回实际像素尺寸（供布局定位）。对齐 mac CaptionRenderer.render(fontSize:)。
    /// </summary>
    public static CaptionImage RenderToFile(string text, int canvasWidth, bool withBackdrop, float fontSize, string outPath)
    {
        text = (text ?? string.Empty).Trim();
        canvasWidth = Math.Max(2, canvasWidth);
        fontSize = Math.Max(12f, fontSize);

        // 固定侧边留白 + 垂直留白随字号放大（保证贴字胶囊上下不裁切）。对齐 mac render()。
        const float sidePadding = 28f;
        var vPadding = Math.Max(18f, fontSize * 0.20f);
        var textMaxWidth = Math.Max(1, canvasWidth - (int)(sidePadding * 2));

        using var font = new Font(FontFamily, fontSize, FontStyle.Bold, GraphicsUnit.Pixel);

        // 先量文本换行后的尺寸
        var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            FormatFlags = 0, // 允许换行
            Trimming = StringTrimming.None,
        };

        SizeF measured;
        using (var tmp = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(tmp))
        {
            mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            measured = mg.MeasureString(text, font, textMaxWidth, format);
        }

        var textH = (int)Math.Ceiling(measured.Height);
        var textW = (int)Math.Ceiling(measured.Width);
        var canvasHeight = textH + (int)Math.Ceiling(vPadding * 2);

        using var bmp = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            if (withBackdrop)
            {
                // 贴字圆角胶囊（padX≈字号0.35 / padY≈字号0.18 / opacity 0.5 / 圆角≈字号0.25），
                // 按最宽行取宽、非通栏满宽，保证「预览=成片」。对齐 mac render() withBackdrop 分支。
                var padX = fontSize * 0.35f;
                var padY = fontSize * 0.18f;
                var pillW = Math.Min(canvasWidth, (int)Math.Ceiling(textW + padX * 2));
                var pillH = Math.Min(canvasHeight, (int)Math.Ceiling(textH + padY * 2));
                var pillX = (canvasWidth - pillW) / 2;
                var pillY = (canvasHeight - pillH) / 2;
                var radius = (int)Math.Min(fontSize * 0.25f, pillH / 2f);
                using var backdrop = new SolidBrush(Color.FromArgb(128, 0, 0, 0)); // opacity 0.5
                using var path = RoundedRect(new Rectangle(pillX, pillY, pillW, pillH), radius);
                g.FillPath(backdrop, path);
            }

            var layoutRect = new RectangleF(sidePadding, (canvasHeight - textH) / 2f, textMaxWidth, textH);

            // 黑色描边（四向偏移）提升任意画面上的可读性
            var outline = Math.Max(1, (int)(fontSize * 0.06f));
            using (var black = new SolidBrush(Color.FromArgb(230, 0, 0, 0)))
            {
                for (var dx = -outline; dx <= outline; dx++)
                for (var dy = -outline; dy <= outline; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    g.DrawString(text, font, black, new RectangleF(layoutRect.X + dx, layoutRect.Y + dy, layoutRect.Width, layoutRect.Height), format);
                }
            }
            using (var white = new SolidBrush(Color.White))
            {
                g.DrawString(text, font, white, layoutRect, format);
            }
        }

        bmp.Save(outPath, ImageFormat.Png);
        return new CaptionImage(outPath, canvasWidth, canvasHeight);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>字幕 PNG 在画面上的叠放位置。对应 mac CaptionLayout。</summary>
public static class CaptionLayout
{
    /// <summary>
    /// 计算字幕 PNG 的 overlay 原点（左上角像素）。有遮挡框时水平、竖直均居中于遮挡带
    /// （字幕永远落在遮挡区正中，对齐 mac CaptionLayout.overlayOrigin）；无遮挡框（直接烧录）
    /// 时水平居中于整幅、纵向落底部 ~82%。最后钳进画面避免出界。
    /// </summary>
    public static (int X, int Y) OverlayOrigin(int outputWidth, int outputHeight, SubtitleMaskRect maskRect,
        int captionWidth, int captionHeight)
    {
        var maxX = Math.Max(0, outputWidth - captionWidth);
        var maxY = Math.Max(0, outputHeight - captionHeight);

        if (maskRect.Width > 0 && maskRect.Height > 0)
        {
            // 居中于遮挡带（水平 + 垂直）
            var band = ToPixelRect(maskRect, outputWidth, outputHeight);
            var rawX = band.X + (band.Width - captionWidth) / 2.0;
            var rawY = band.Y + (band.Height - captionHeight) / 2.0;
            var bx = (int)Math.Round(Math.Min(Math.Max(rawX, 0), maxX));
            var by = (int)Math.Round(Math.Min(Math.Max(rawY, 0), maxY));
            return (bx, by);
        }

        // 无遮挡框（直接烧录）：水平居中于整幅、纵向底部 ~82%
        var x = Math.Max(0, (outputWidth - captionWidth) / 2);
        var y = Math.Clamp((int)Math.Round(outputHeight * 0.82) - captionHeight, 0, maxY);
        return (x, y);
    }

    /// <summary>归一化遮挡矩形 → 像素并取整（宽高至少 2px、整体钳进画面）。对齐 mac PixelRect.from。</summary>
    private static (int X, int Y, int Width, int Height) ToPixelRect(SubtitleMaskRect rect, int outputWidth, int outputHeight)
    {
        var c = rect.Clamped();
        var w = Math.Max(2, Math.Min((int)Math.Round(c.Width * outputWidth), outputWidth));
        var h = Math.Max(2, Math.Min((int)Math.Round(c.Height * outputHeight), outputHeight));
        var x = Math.Max(0, Math.Min((int)Math.Round(c.X * outputWidth), outputWidth - w));
        var y = Math.Max(0, Math.Min((int)Math.Round(c.Y * outputHeight), outputHeight - h));
        return (x, y, w, h);
    }
}
