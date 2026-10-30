using Tmds.DBus.Protocol;

namespace SCSKiller.Ui.Platform;

/// <summary>Stops idle sleep, not a suspend the user asks for: the Inhibit portal on the session bus (Suspend | Idle) and
/// logind's "idle" block lock, both. One that fails is logged once and skipped, and the compile goes on.</summary>
sealed class IdleInhibitor(Func<Login1?> login, Action<string> log) : IDisposable
{
    const string Why = "Compiling shaders", Portal = "org.freedesktop.portal.Desktop", PortalPath = "/org/freedesktop/portal/desktop";

    readonly Lock _order = new();
    Task _chain = Task.CompletedTask;
    DBusConnection? _bus;
    Func<Task>? _portal;
    IDisposable? _logind;
    bool _want;
    readonly HashSet<string> _logged = [];

    /// <summary>In order and off the caller's thread, so it never blocks. Holding twice or releasing twice does nothing.</summary>
    public void Hold(bool on) => Enqueue(() => { _want = on; return on ? TakeAsync() : ReleaseAsync(); });

    /// <summary>logind connected after the hold began: take its lock too, if the hold is still wanted.</summary>
    public void Retry() => Enqueue(() => _want ? TakeAsync() : Task.CompletedTask);

    void Enqueue(Func<Task> work)
    {
        lock (_order) _chain = _chain.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    // KDE's portal maps Idle to a dim/lock policy, not to auto-suspend: logind's lock is taken alongside it
    async Task TakeAsync()
    {
        if (_portal == null)
        {
            try { _portal = await PortalAsync().ConfigureAwait(false); }
            catch (Exception) { }
            if (_portal == null) LogOnce("portal");
        }
        if (_logind == null)
        {
            try
            {
                var lockFd = login() is { } l ? await l.InhibitAsync("idle", "block", Why).ConfigureAwait(false) : null;
                if (lockFd != null) _logind = lockFd;
            }
            catch (Exception) { }
            if (_logind == null && login() != null) LogOnce("logind");   // not connected yet: Retry follows
        }
    }

    void LogOnce(string who)
    {
        if (_logged.Add(who)) log($"no idle inhibitor from {who} while compiling: refused");
    }

    async Task<Func<Task>?> PortalAsync()
    {
        if (DBusAddress.Session is not { } address) return null;
        if (_bus == null)
        {
            var bus = new DBusConnection(address);
            try { await bus.ConnectAsync().ConfigureAwait(false); }
            catch { bus.Dispose(); throw; }
            _bus = bus;
        }
        var request = await _bus.CallMethodAsync(Inhibit(_bus), (m, _) => m.GetBodyReader().ReadObjectPathAsString(), null).ConfigureAwait(false);
        return () => _bus.CallMethodAsync(Close(_bus, request));
    }

    static MessageBuffer Inhibit(DBusConnection bus)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(destination: Portal, path: PortalPath, @interface: "org.freedesktop.portal.Inhibit", member: "Inhibit",
            signature: "sua{sv}", flags: MessageFlags.None);
        w.WriteString("");
        w.WriteUInt32(12);   // Suspend | Idle
        w.WriteDictionary(new Dictionary<string, VariantValue> { ["reason"] = Why });
        return w.CreateMessage();
    }

    static MessageBuffer Close(DBusConnection bus, string request)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(destination: Portal, path: request, @interface: "org.freedesktop.portal.Request", member: "Close", flags: MessageFlags.None);
        return w.CreateMessage();
    }

    async Task ReleaseAsync()
    {
        var (portal, logind) = (_portal, _logind);
        (_portal, _logind) = (null, null);
        try { logind?.Dispose(); }
        catch (Exception) { }
        if (portal == null) return;
        try { await portal().ConfigureAwait(false); }
        catch (Exception) { }   // the bus is gone: the portal drops a request whose connection closed
    }

    public void Dispose()
    {
        Hold(false);
        try { _chain.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception) { }
        _bus?.Dispose();
    }
}
