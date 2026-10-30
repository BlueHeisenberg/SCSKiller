using SCSKiller.Core.App;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui;

/// <summary>Start with Windows: the HKCU Run value (<see cref="WindowsStartup"/>), real data only.</summary>
public partial class App
{
#if DEBUG
    const bool ReleaseBuild = false;
#else
    const bool ReleaseBuild = true;
#endif

    /// <summary><paramref name="byUser"/>: Settings' toggle changed, otherwise the start's reconcile. Debug builds leave the
    /// registry as it is.</summary>
    static Task ApplyStartWithWindows(bool byUser)
    {
        if (Environment.ProcessPath is not { } exe) return Task.CompletedTask;
        WindowsStartup.Apply(byUser, ReleaseBuild, Host.Core.Settings.StartWithWindows, exe, ReleaseBuild && Updater.Installed && !AppStore.IsPortable,
            WindowsStartup.ReadRegistry, WindowsStartup.WriteRegistry, File.Exists);
        return Task.CompletedTask;
    }

    /// <summary>Settings shows the setting.</summary>
    static bool? StartsAtSignIn() => null;

    /// <summary>None on Windows: the task runs the CLI itself.</summary>
    public static int? RunCli(string[] args) => null;
}
