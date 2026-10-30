using System.Runtime.InteropServices;
using Avalonia.Threading;
using SCSKiller.Core;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Platform;

/// <summary>Windows ends the session through the tray's window (WM_QUERYENDSESSION) and Avalonia's ShutdownRequested. The PC
/// stays awake while a compile runs.</summary>
static class Session
{
    const uint EsContinuous = 0x80000000, EsSystemRequired = 1;
    static AwakeGuard? awake;

    // SetThreadExecutionState belongs to the calling thread: set and clear it on the UI thread, which lives as long as the app
    public static void Watch(IScsKiller core, Action end, Action now, Action<string> log) =>
        awake = new(core, on => Dispatcher.UIThread.Post(() => SetThreadExecutionState(on ? EsContinuous | EsSystemRequired : EsContinuous)));

    public static void Quitting() { }

    public static void Dispose() => awake?.Dispose();

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
}
