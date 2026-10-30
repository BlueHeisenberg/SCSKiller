using SCSKiller.Core;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Platform;

/// <summary>The session's end on Linux: the quit signals, and logind's shutdown and sleep while a compile runs, and its idle inhibitor.</summary>
static class Session
{
    static readonly Lock gate = new();
    static QuitSignals? signals;
    static Login1? login;
    static ShutdownGuard? guard;
    static IdleInhibitor? inhibitor;
    static AwakeGuard? awake;
    static bool quitting, ended;

    /// <summary><paramref name="end"/> on the first quit signal, <paramref name="now"/> on each later one, on another thread.</summary>
    public static void Watch(IScsKiller core, Action end, Action now, Action<string> log)
    {
        signals = new QuitSignals(s => { log($"{s}: quitting"); end(); }, s => { log($"{s} again: quitting now"); now(); });
        inhibitor = new IdleInhibitor(() => { lock (gate) return login; }, log);
        awake = new AwakeGuard(core, inhibitor.Hold);
        _ = GuardAsync(core, log);
    }

    static async Task GuardAsync(IScsKiller core, Action<string> log)
    {
        Login1 bus;
        try { bus = await Login1.ConnectAsync(); }
        catch (IOException e)
        {
            log($"no shutdown lock while compiling: {e.Message}");
            return;
        }
        lock (gate)
            if (!ended)
            {
                (login, guard) = (bus, new ShutdownGuard(core, bus, log));
                inhibitor?.Retry();
                if (quitting) guard.Quit();
                return;
            }
        bus.Dispose();   // the app ended while it connected
    }

    /// <summary>A quit signal: no wake's resume starts the queue again.</summary>
    public static void Quitting()
    {
        lock (gate)
        {
            quitting = true;
            guard?.Quit();
        }
    }

    public static void Dispose()
    {
        lock (gate) ended = true;
        awake?.Dispose();
        inhibitor?.Dispose();
        guard?.Dispose();
        login?.Dispose();
        signals?.Dispose();
    }
}
