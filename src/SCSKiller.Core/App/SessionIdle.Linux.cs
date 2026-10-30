using Tmds.DBus.Protocol;

namespace SCSKiller.Core.App;

/// <summary>How long the user has been away from keyboard and mouse on Linux, for When idle: GNOME's or KDE's idle monitor
/// on the session bus, else logind's idle hint of the user's graphical session on the system bus. MaxValue when none
/// answers. The watch loop polls it from sync code: an answer is kept for a second, a call waits half a second at most and
/// never throws.</summary>
static class SessionIdle
{
    const string Properties = "org.freedesktop.DBus.Properties", Login1 = "org.freedesktop.login1", Session = "org.freedesktop.login1.Session";
    const int SessionBus = 0, SystemBus = 1;
    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(1), Wait = TimeSpan.FromMilliseconds(500), CallWait = TimeSpan.FromMilliseconds(400);

    static readonly Lock _lock = new();
    static Task? _query;
    static TimeSpan _last = TimeSpan.MaxValue;
    static long _at = long.MinValue / 2;
    static bool _logged;
    // only the one query in flight touches them
    static readonly DBusConnection?[] _buses = new DBusConnection?[2];

    /// <summary>Told once which source answers, or that none does.</summary>
    public static Action<string>? Log { get; set; }

    public static TimeSpan Now()
    {
        Task q;
        lock (_lock)
        {
            if (_query != null) return _last;   // a slow bus: the last answer, without waiting again
            if (Environment.TickCount64 - _at < Fresh.TotalMilliseconds) return _last;
            q = _query = Task.Run(Query).ContinueWith(Done, TaskScheduler.Default);
        }
        q.Wait(Wait);
        lock (_lock) return _last;
    }

    static void Done(Task<(TimeSpan Idle, string? Source)> t)
    {
        var (idle, source) = t.Exception == null ? t.Result : (TimeSpan.MaxValue, null);
        bool first;
        lock (_lock)
        {
            (_last, _at, _query) = (idle, Environment.TickCount64, null);
            first = !_logged;
            _logged = true;
        }
        if (!first) return;
        try { Log?.Invoke(source == null ? "When idle: no idle time is known on this desktop, so idle work doesn't wait for you to be away (games still pause it)" : $"When idle: the idle time is {source}'s"); }
        catch (Exception) { }
    }

    static async Task<(TimeSpan, string?)> Query()
    {
        // milliseconds since the last input
        if (await Try(SessionBus, async c => (ulong?)await Call(c, "org.gnome.Mutter.IdleMonitor", "/org/gnome/Mutter/IdleMonitor/Core", "org.gnome.Mutter.IdleMonitor",
                "GetIdletime", (m, _) => m.GetBodyReader().ReadUInt64())) is { } gnome) return (TimeSpan.FromMilliseconds(gnome), "GNOME");
        // KDE answers in milliseconds (KIdleTime's), though the spec says seconds. On Wayland it refuses.
        if (await Try(SessionBus, async c => (ulong?)await Call(c, "org.freedesktop.ScreenSaver", "/ScreenSaver", "org.freedesktop.ScreenSaver",
                "GetSessionIdleTime", (m, _) => m.GetBodyReader().ReadUInt32())) is { } kde) return (TimeSpan.FromMilliseconds(kde), "KDE");
        if (await Try(SystemBus, Logind) is { } us) return (TimeSpan.FromMicroseconds(us), "logind");
        return (TimeSpan.MaxValue, null);
    }

    /// <summary>µs idle by logind. A user service runs in no session, so session/auto isn't one: user/self's Display is.
    /// logind knows a session's idle state only from its desktop (SetIdleHint), and the time stays 0 until the hint first
    /// changes: null then.</summary>
    static async Task<ulong?> Logind(DBusConnection c)
    {
        var session = await Call(c, Login1, "/org/freedesktop/login1/user/self", Properties, "Get",
            (m, _) => m.GetBodyReader().ReadVariantValue().GetItem(1).GetObjectPathAsString(), "org.freedesktop.login1.User", "Display");
        if (session is "" or "/") return null;
        var since = await Call(c, Login1, session, Properties, "Get", (m, _) => m.GetBodyReader().ReadVariantValue().GetUInt64(), Session, "IdleSinceHint");
        if (since == 0) return null;
        if (!await Call(c, Login1, session, Properties, "Get", (m, _) => m.GetBodyReader().ReadVariantValue().GetBool(), Session, "IdleHint")) return 0;
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;   // IdleSinceHint is CLOCK_REALTIME
        return now > since ? now - since : 0;
    }

    static async Task<T> Call<T>(DBusConnection c, string service, string path, string iface, string member, MessageValueReader<T> read, params string[] args)
    {
        MessageBuffer message;
        using (var w = c.GetMessageWriter())
        {
            // never starts a service that isn't running
            w.WriteMethodCallHeader(service, path, iface, member, args.Length == 0 ? null : new string('s', args.Length), MessageFlags.NoAutoStart);
            foreach (var a in args) w.WriteString(a);
            message = w.CreateMessage();
        }
        var call = c.CallMethodAsync(message, read, null);
        await Bounded(call).ConfigureAwait(false);
        return call.Result;
    }

    // what is left waiting is observed: its fault when the connection goes would be logged as a crash
    static async Task Bounded(Task t)
    {
        try { await t.WaitAsync(CallWait).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _ = t.ContinueWith(x => _ = x.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>Null when the bus or the service doesn't answer. A connection that failed is dropped, and made again next time.</summary>
    static async Task<T?> Try<T>(int bus, Func<DBusConnection, Task<T?>> read) where T : struct
    {
        try
        {
            if (_buses[bus] is not { } c)
            {
                var address = (bus == SystemBus ? DBusAddress.System : SessionAddress()) ?? throw new InvalidOperationException("no bus");
                _buses[bus] = c = new DBusConnection(address);
                await Bounded(c.ConnectAsync().AsTask()).ConfigureAwait(false);
            }
            return await read(c).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException) { return null; }   // no such service, object or property, or refused
        catch (Exception)
        {
            _buses[bus]?.Dispose();
            _buses[bus] = null;
            return null;
        }
    }

    static string? SessionAddress() =>
        Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is { Length: > 0 } a ? a
        : Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run && Path.Exists(Path.Combine(run, "bus")) ? $"unix:path={Path.Combine(run, "bus")}" : null;
}
