using System;
using System.IO;
using MixCut.Models;
using Xunit;

namespace MixCut.Tests.Models;

/// <summary>
/// #12 Segment.EffectivePicture「有效画面单一真源」不变式回归测试。
/// 卡片取图 / 预览 / 所有导出路径都读它——原/替换择一逻辑只有这一处，必须稳。
/// </summary>
public class EffectivePictureTests
{
    private static Segment MakeSegment() => new()
    {
        Id = Guid.NewGuid(),
        StartFrame = 300, EndFrame = 780, Fps = 30,          // 16s @30fps
        StartTime = 10.0, EndTime = 26.0,
        ThumbnailPath = "C:/fake/orig.jpg",
        Video = new Video { LocalPath = "C:/fake/source.mp4", Fps = 30 },
    };

    [Fact]
    public void NoReplacement_ReturnsOriginalSource()
    {
        var seg = MakeSegment();
        var ep = seg.EffectivePicture;
        Assert.False(ep.IsReplaced);
        Assert.Equal("C:/fake/source.mp4", ep.VideoPath);
        Assert.Equal(300, ep.StartFrame);
        Assert.Equal(780, ep.EndFrame);
        Assert.Equal(30, ep.Fps);
        Assert.Equal("C:/fake/orig.jpg", ep.ThumbnailPath);
    }

    [Fact]
    public void ShowsReplacedButFileMissing_FallsBackToOriginal()
    {
        // 标记显示替换画面，但替换文件不在磁盘 → 优雅回退原画面（防导出/预览打不开）。
        var seg = MakeSegment();
        seg.ReplacedPictureVideoPath = "C:/does/not/exist.mp4";
        seg.ReplacedPictureFrameCount = 480;
        seg.PictureShowsReplaced = true;
        var ep = seg.EffectivePicture;
        Assert.False(ep.IsReplaced);
        Assert.Equal("C:/fake/source.mp4", ep.VideoPath);
    }

    [Fact]
    public void ShowsReplacedWithExistingFile_ReturnsReplacedWholeClip()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mixcut-ep-test-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(tmp, new byte[] { 0, 1, 2 });
        try
        {
            var seg = MakeSegment();
            seg.ReplacedPictureVideoPath = tmp;
            seg.ReplacedPictureThumbnailPath = "C:/fake/rep.jpg";
            seg.ReplacedPictureFrameCount = 480;    // 16s @30fps 拼接后
            seg.PictureShowsReplaced = true;

            var ep = seg.EffectivePicture;
            Assert.True(ep.IsReplaced);
            Assert.Equal(tmp, ep.VideoPath);
            Assert.Equal(0, ep.StartFrame);          // 替换片是整段独立片，从 0 起
            Assert.Equal(480, ep.EndFrame);
            Assert.Equal("C:/fake/rep.jpg", ep.ThumbnailPath);
            // fps 由 帧数/时长 反推（concat 统一 30fps，与源可不同）；这里 480/16s = 30。
            Assert.Equal(30, ep.Fps, precision: 3);
            Assert.Equal(seg.Duration, ep.EndTime, precision: 3);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void HasReplacedButShowingOriginal_ReturnsOriginal()
    {
        // 有替换画面文件，但当前切回原画面（pictureShowsReplaced=false）→ 返回原画面。
        var tmp = Path.Combine(Path.GetTempPath(), $"mixcut-ep-test-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(tmp, new byte[] { 0 });
        try
        {
            var seg = MakeSegment();
            seg.ReplacedPictureVideoPath = tmp;
            seg.ReplacedPictureFrameCount = 480;
            seg.PictureShowsReplaced = false;        // 切回原画面
            Assert.False(seg.EffectivePicture.IsReplaced);
            Assert.Equal("C:/fake/source.mp4", seg.EffectivePicture.VideoPath);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void InvalidateReplacedPicture_ClearsAllFields()
    {
        var seg = MakeSegment();
        seg.ReplacedPictureVideoPath = "C:/x.mp4";
        seg.ReplacedPictureThumbnailPath = "C:/x.jpg";
        seg.ReplacedPictureFrameCount = 480;
        seg.PictureShowsReplaced = true;

        seg.InvalidateReplacedPicture();

        Assert.Null(seg.ReplacedPictureVideoPath);
        Assert.Null(seg.ReplacedPictureThumbnailPath);
        Assert.Equal(0, seg.ReplacedPictureFrameCount);
        Assert.False(seg.PictureShowsReplaced);
        Assert.False(seg.HasReplacedPicture);
    }
}
