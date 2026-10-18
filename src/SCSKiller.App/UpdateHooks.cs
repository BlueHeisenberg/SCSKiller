using SCSKiller.Core.App;
using Velopack;
using Velopack.Logging;

namespace SCSKiller.App;

/// <summary>Velopack's lifecycle hooks and its logger, and updates.log. Velopack logs from inside Run, before it sets its
/// locator, so nothing here touches <see cref="Updater"/>, whose state builds UpdateManagers, which need that locator.</summary>
public static class UpdateHooks
{
    // read when a line is written, not when the type loads: a portable build's first start copies its data there
    public static string DataDir => AppStore.DefaultDir;

    // every step of an update, in the data folder: up to 128 KB, then one older file
    public static void Log(string line) => ScsKiller.AppendLog(DataDir, "updates.log", line, 128 * 1024);

    /// <summary>Velopack's own messages, Information and up, in <see cref="Log"/>.</summary>
    sealed class VelopackLog : IVelopackLogger
    {
        public void Log(VelopackLogLevel level, string? message, Exception? exception)
        {
            if (level >= VelopackLogLevel.Information) UpdateHooks.Log($"Velopack {level}: {message}{(exception == null ? "" : $" ({exception.GetType().Name}: {exception.Message})")}");
        }
    }

    /// <summary>The hooks Program.Main runs before anything else: Update.exe runs the install, update and uninstall hooks
    /// through it, fast, then exits. Each step on its own (<see cref="HookSteps"/>).</summary>
    public static VelopackApp Hooks(VelopackApp app) => app
        .SetLogger(new VelopackLog())   // Run's locator keeps it: every UpdateManager of this process logs through it
        .SetAutoApplyOnStartup(false)   // it takes the newest package on disk, any channel, before any Busy or offline check, and force-stops every process under the install root
        .OnAfterInstallFastCallback(v => HookSteps.Run($"Installed {v}", Log,
            // a zip install's driver-update task points at the zip's folder: move it here (current\ keeps its name across updates)
            () => { if (ScheduledTask.Registered && ScheduledTask.TaskExe() is { } exe) ScheduledTask.Register(exe); }))
        .OnAfterUpdateFastCallback(v => HookSteps.Run($"Update.exe swapped in {v}", Log,
            () => Busy.ClearApplying(DataDir)))   // the swap is done: the CLI may run again
        // Kept: the data dir (recordings, settings).
        .OnBeforeUninstallFastCallback(_ => HookSteps.Uninstall(new AppStore(DataDir), Log, ScheduledTask.Unregister,
            () => { if (Environment.ProcessPath is { } app) WindowsStartup.Set(false, app, WindowsStartup.ReadRegistry, WindowsStartup.WriteRegistry); }));
}
