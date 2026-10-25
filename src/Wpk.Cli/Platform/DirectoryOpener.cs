using System.Diagnostics;
using Wpk.Core;

namespace Wpk.Cli.Platform;

public sealed class DirectoryOpener : IDirectoryOpener
{
    public void Open(string directory)
    {
        var path = Identity.PathKey(directory) ?? throw new InvalidOperationException("安装目录不是绝对路径。");
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("安装目录不存在。");
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        { UseShellExecute = false };
        info.ArgumentList.Add(path);
        using var process = Process.Start(info);
    }
}
