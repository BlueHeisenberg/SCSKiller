using System.Diagnostics;

namespace SCSKiller.Core.Games;

public static class ShowExe
{
    // Explorer selects the exe on Windows, elsewhere the folder opens. False when the folder is gone.
    public static bool Open(string exe)
    {
        var dir = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
        using var _ = OperatingSystem.IsWindows() && File.Exists(exe)
            ? Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{exe}\"" })
            : Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        return true;
    }
}
