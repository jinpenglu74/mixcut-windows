using System.IO;

namespace MixCut.Utilities;

/// <summary>
/// 目录内文件重名自动加后缀：「歌.mp3」→「歌 2.mp3」→「歌 3.mp3」（从 2 递增，空格分隔）。
/// 对应 mac UniqueFileNamer（BGM 库上传用，issue #22）。纯函数便于单测。
/// </summary>
public static class UniqueFileNamer
{
    /// <summary>
    /// 返回在 <paramref name="directory"/> 下不冲突的文件名（含扩展名，不含路径）。
    /// 无冲突原样返回；冲突则在主名后加「 2」「 3」…；无扩展名的文件同样正确处理。
    /// </summary>
    public static string MakeUnique(string directory, string fileName)
    {
        if (!File.Exists(Path.Combine(directory, fileName)))
        {
            return fileName;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName); // 含点；无扩展名时为 ""
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} {n}{ext}";
            if (!File.Exists(Path.Combine(directory, candidate)))
            {
                return candidate;
            }
        }
    }
}
