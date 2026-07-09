namespace MixCut.Services.ShotEdit;

/// <summary>一个镜头的帧区间（纯值）。对应 mac ShotSpan。</summary>
public readonly record struct ShotSpan(int StartFrame, int EndFrame)
{
    public int FrameCount => Math.Max(0, EndFrame - StartFrame);
}

/// <summary>
/// 分镜头编辑纯算法：以「切分点建模」实现合并 / 拆分 / 移动边界，结构上保证
/// 首尾无缝铺满、总时长恒等（不可能出现空隙/重叠/总长不符）。最小 5 帧。
/// 纯函数、零外部依赖，便于单测。对应 macOS MixCutCore.ShotPartitionEditor。
/// </summary>
public static class ShotPartitionEditor
{
    /// <summary>移动边界 / 拆分的最小帧数。对应 mac defaultMinFrames。</summary>
    public const int DefaultMinFrames = 5;

    /// <summary>合并第 i 与 i+1 个镜头 → 新镜头 = [shots[i].start, shots[i+1].end]。越界原样返回。</summary>
    public static IReadOnlyList<ShotSpan> Merge(IReadOnlyList<ShotSpan> shots, int i)
    {
        if (i < 0 || i + 1 >= shots.Count) return shots;
        var outList = new List<ShotSpan>(shots);
        var merged = new ShotSpan(shots[i].StartFrame, shots[i + 1].EndFrame);
        outList[i] = merged;
        outList.RemoveAt(i + 1);
        return outList;
    }

    /// <summary>
    /// 移动第 <paramref name="boundaryIndex"/> 个边界（介于镜头 b 与 b+1 之间）到目标帧 <paramref name="toFrame"/>。
    /// clamp 到 [shots[b].start + minFrames, shots[b+1].end - minFrames]（保证两侧各 ≥ minFrames 帧）。
    /// 若 clamp 区间为空则原样返回。
    /// </summary>
    public static IReadOnlyList<ShotSpan> MoveBoundary(
        IReadOnlyList<ShotSpan> shots, int boundaryIndex, int toFrame, int minFrames = DefaultMinFrames)
    {
        var b = boundaryIndex;
        if (b < 0 || b + 1 >= shots.Count) return shots;
        var lo = shots[b].StartFrame + minFrames;
        var hi = shots[b + 1].EndFrame - minFrames;
        if (hi < lo) return shots;
        var nf = Math.Clamp(toFrame, lo, hi);
        var outList = new List<ShotSpan>(shots);
        outList[b] = new ShotSpan(shots[b].StartFrame, nf);
        outList[b + 1] = new ShotSpan(nf, shots[b + 1].EndFrame);
        return outList;
    }

    /// <summary>在第 i 个镜头的 <paramref name="atFrame"/> 处一分为二。要求 atFrame ∈ [start+min, end-min]，否则原样返回。</summary>
    public static IReadOnlyList<ShotSpan> Split(
        IReadOnlyList<ShotSpan> shots, int i, int atFrame, int minFrames = DefaultMinFrames)
    {
        if (i < 0 || i >= shots.Count) return shots;
        var s = shots[i];
        if (atFrame < s.StartFrame + minFrames || atFrame > s.EndFrame - minFrames) return shots;
        var outList = new List<ShotSpan>(shots);
        outList[i] = new ShotSpan(s.StartFrame, atFrame);
        outList.Insert(i + 1, new ShotSpan(atFrame, s.EndFrame));
        return outList;
    }

    /// <summary>把第 i 个镜头从正中间一分为二（UI「拆分」按钮用）。</summary>
    public static IReadOnlyList<ShotSpan> SplitAtMidpoint(IReadOnlyList<ShotSpan> shots, int i, int minFrames = DefaultMinFrames)
    {
        if (i < 0 || i >= shots.Count) return shots;
        var s = shots[i];
        var mid = (s.StartFrame + s.EndFrame) / 2;
        return Split(shots, i, mid, minFrames);
    }
}
