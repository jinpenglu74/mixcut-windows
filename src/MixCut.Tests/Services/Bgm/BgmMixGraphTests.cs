using MixCut.Services.Bgm;
using Xunit;

namespace MixCut.Tests.Services.Bgm;

/// <summary>issue #22：整片铺 BGM 滤镜图纯函数（截断边界 / 音量夹取 / 短片淡出，对齐 mac 单测）。</summary>
public class BgmMixGraphTests
{
    [Fact]
    public void 常规时长_截断到成片时长_结尾1秒淡出()
    {
        var g = BgmMixGraph.Build(5.0, 0.6);
        Assert.Contains("atrim=0:5.000", g);
        Assert.Contains("volume=0.60", g);
        Assert.Contains("afade=t=out:st=4.000:d=1.000", g);
        Assert.Contains("amix=inputs=2:duration=first:normalize=0[aout]", g);
    }

    [Fact]
    public void 音量超界被夹取()
    {
        Assert.Contains("volume=1.00", BgmMixGraph.Build(10, 1.7));
        Assert.Contains("volume=0.00", BgmMixGraph.Build(10, -0.2));
    }

    [Fact]
    public void 成片短于1秒_淡出从0开始且不超时长()
    {
        var g = BgmMixGraph.Build(0.5, 0.6);
        Assert.Contains("afade=t=out:st=0.000:d=0.500", g);
        Assert.Contains("atrim=0:0.500", g);
    }
}
