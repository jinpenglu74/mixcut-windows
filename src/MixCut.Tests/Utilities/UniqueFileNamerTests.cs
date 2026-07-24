using System.IO;
using MixCut.Utilities;
using Xunit;

namespace MixCut.Tests.Utilities;

/// <summary>issue #22：BGM 库上传重名自动加后缀（对齐 mac UniqueFileNamer 的三条单测）。</summary>
public class UniqueFileNamerTests : IDisposable
{
    private readonly string _dir;

    public UniqueFileNamerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mixcut-test-namer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 尽力 */ }
    }

    [Fact]
    public void 无冲突时原样返回()
    {
        Assert.Equal("歌.mp3", UniqueFileNamer.MakeUnique(_dir, "歌.mp3"));
    }

    [Fact]
    public void 冲突时从2递增()
    {
        File.WriteAllText(Path.Combine(_dir, "歌.mp3"), "x");
        Assert.Equal("歌 2.mp3", UniqueFileNamer.MakeUnique(_dir, "歌.mp3"));

        File.WriteAllText(Path.Combine(_dir, "歌 2.mp3"), "x");
        Assert.Equal("歌 3.mp3", UniqueFileNamer.MakeUnique(_dir, "歌.mp3"));
    }

    [Fact]
    public void 无扩展名也正确()
    {
        File.WriteAllText(Path.Combine(_dir, "readme"), "x");
        Assert.Equal("readme 2", UniqueFileNamer.MakeUnique(_dir, "readme"));
    }
}
