using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui;

/// <summary>The notification-area icon, the close button, the quit and the session's end, on the real core only (a design
/// run never adds an icon beside the installed app's).</summary>
public partial class App
{
    /// <summary>At the session's end (shutdown, restart, sign-out), how long a running compile gets to stop before Windows
    /// goes on (it won't wait much longer); the warm job then kills what is left. A stopped warm exits once its in-flight
    /// items finish; the NVIDIA driver then writes its cache as the process exits, which took ~4 min after a full Jedi
    /// Survivor warm (3.49 GB). Ending the process earlier loses what the driver hadn't written: the game compiles those
    /// pipelines itself when it meets them, and the next warm redoes them, since an ended run keeps its old resume point.
    /// AMD writes during the run: only the in-flight items are lost. The tray's Quit waits instead.</summary>
    static readonly TimeSpan SessionEndWait = TimeSpan.FromSeconds(10);

    static Tray? tray;
    static readonly QuitFlow quit = new();

    static void CreateTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var core = Host.Core;
        tray = new Tray(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"))
        {
            Open = ShowWindow,
            PauseLabel = () => QuitFlow.Running(core) is not { } q ? null : q.Stage == QueueStage.Paused ? "Resume" : "Pause compiling",
            PauseOrResume = () => Fmt.PauseOrResume(QuitFlow.Running(core)),
            Quit = () => _ = QuitAsync(desktop),
            QuitLabel = () => quit.Label,
            SessionEnding = SessionEnding,
            SessionEnd = SessionEnd,
        };
        UpdateTip();
        WatchTrayHost();
        var tip = new Coalesced(Host.Ui, UpdateTip);
        core.QueueChanged += _ => tip.Request();
        core.GameChanged += _ => tip.Request();
        if (core is ScsKiller k) k.ScanProgressed += tip.Request;   // a fold the queue doesn't run

        Main.Closing += (_, e) =>
        {
            if (e.CloseReason != WindowCloseReason.WindowClosing) return;   // the session's end or the quit's own shutdown
            // never closed: without an icon (refused, or no tray host on Linux) the close quits gracefully
            e.Cancel = true;
            Main.Hide();
            if (WindowClose.Of(core.Settings, quit.Quitting, tray is { Added: true }) != CloseAction.Hide) _ = QuitAsync(desktop);
            else Activation.TrayNotice();
        };
        // Avalonia's own hidden window gets the session's end too and exits at once on it: stop and wait first
        desktop.ShutdownRequested += (_, _) =>
        {
            var hwnd = Main.TryGetPlatformHandle()?.Handle ?? 0;
            SessionEnding(hwnd);
            SessionEnd(hwnd);
        };
        var signal = new SignalQuit
        {
            CallOff = Platform.Session.Quitting,
            Hide = () => Main.Hide(),
            Flush = async () =>
            {
                SetExitReason("quit signal");
                if (core is ScsKiller { Log: AppLog log }) await log.FlushAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            },
            End = () =>
            {
                DisposeTray();
                ReportWindow.CloseAll();
                Platform.Notifications.Unregister();
                Platform.Session.Dispose();
                desktop.Shutdown();
            },
            Wait = SessionEndWait,
        };
        Platform.Session.Watch(core, () => _ = signal.RunAsync(core, Host.Ui), () => Environment.Exit(1),
            line => (core as ScsKiller)?.Log?.Report(line));
    }

    /// <summary>Linux: a hidden window comes back when no tray host shows the icon.</summary>
    static partial void WatchTrayHost();

    /// <summary>Brings the window back from the notification area (or from behind other windows).</summary>
    internal static void ShowWindow()
    {
        Main.Show();
        if (Main.WindowState == WindowState.Minimized) Main.WindowState = WindowState.Normal;   // SW_RESTORE: back to maximized if it was
        Main.Activate();
        Platform.SingleInstance.ToFront(Main);
    }

    static void UpdateTip()
    {
        if (tray == null) return;
        var q = QuitFlow.Running(Host.Core);
        tray.Tip = "SCSKiller: " + (q == null && !quit.Quitting && Fmt.FoldingLine is { } fold ? fold
            : Format.TrayStatus(q, q == null ? null : Host.Core.Games.FirstOrDefault(g => g.Game.Id == q.GameId)?.Game.Name ?? q.GameId, quit.Quitting).Line);
    }

    static void DisposeTray()
    {
        tray?.Dispose();
        tray = null;
    }

    static void SetExitReason(string why, bool over = false)
    {
        if (Host.Core is ScsKiller { Log: AppLog log } && (over || log.ExitReason == "exit")) log.ExitReason = why;
    }

    /// <summary>Windows is about to end the session: start stopping. Each of the app's windows gets it, so it runs again.</summary>
    static void SessionEnding(nint hwnd)
    {
        Main.SavePlacement();
        SetExitReason("session end (shutdown or log-off)");
        if (!Host.Core.Compiling) return;   // a removed item's warm too: it is still saving
        if (OperatingSystem.IsWindows()) ShutdownBlockReasonCreate(hwnd, "Stopping the compile so the graphics driver can save its shader cache");
        QuitFlow.StopAll(Host.Core);
    }

    /// <summary>The session ends once this returns: bounded, then the warm job kills whatever is left.</summary>
    static void SessionEnd(nint hwnd)
    {
        QuitFlow.WaitStopped(Host.Core, SessionEndWait);
        if (Host.Core is ScsKiller { Log: AppLog log }) log.FlushAsync(TimeSpan.FromSeconds(1)).Wait();
        if (OperatingSystem.IsWindows()) ShutdownBlockReasonDestroy(hwnd);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool ShutdownBlockReasonCreate(nint hwnd, string reason);
    [DllImport("user32.dll")] static extern bool ShutdownBlockReasonDestroy(nint hwnd);
}
