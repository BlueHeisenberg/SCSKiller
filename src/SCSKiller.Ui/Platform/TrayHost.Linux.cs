using Tmds.DBus.Protocol;

namespace SCSKiller.Ui.Platform;

/// <summary>Whether this session shows StatusNotifierItem icons: org.kde.StatusNotifierWatcher has an owner and says a
/// host registered. KDE has one, GNOME only with an AppIndicator extension, gamescope none. Followed as hosts come and go.</summary>
sealed class TrayHost : IDisposable
{
    const string Watcher = "org.kde.StatusNotifierWatcher", WatcherPath = "/StatusNotifierWatcher";

    readonly DBusConnection? _bus;
    readonly Action<bool> _changed;
    readonly TimeSpan _wait;
    readonly List<IDisposable> _watches = [];
    readonly Lock _gate = new();
    int _asked;
    bool? _told;
    volatile bool _present;
    string? _owner;   // the watcher's unique name: any peer can send its signals, only its own count
    int _owners;   // owner changes seen, so an older GetNameOwner answer doesn't undo a newer change

    public bool Present => _present;

    TrayHost(string? address, Action<bool> changed, TimeSpan wait) =>
        (_bus, _changed, _wait) = (address == null ? null : new DBusConnection(address), changed, wait);

    /// <summary>Follows the hosts on the bus at <paramref name="address"/> (null: no session bus, so no host).
    /// <paramref name="changed"/> gets the first answer, then each change, on another thread. <paramref name="wait"/>: how
    /// long the watcher has to answer (5 s), a watcher that doesn't is no host.</summary>
    public static TrayHost Watch(string? address, Action<bool> changed, TimeSpan? wait = null)
    {
        var h = new TrayHost(address, changed, wait ?? TimeSpan.FromSeconds(5));
        _ = h.StartAsync();
        return h;
    }

    async Task StartAsync()
    {
        try
        {
            if (_bus == null) throw new InvalidOperationException("no session bus");
            await _bus.ConnectAsync();
            var owner = new MatchRule
            {
                Type = MessageType.Signal, Sender = "org.freedesktop.DBus", Path = "/org/freedesktop/DBus", Interface = "org.freedesktop.DBus",
                Member = "NameOwnerChanged", Arg0 = Watcher,
            };
            // only the bus itself sends as org.freedesktop.DBus
            _watches.Add(await _bus.AddMatchAsync<string>(owner, static (m, _) =>
            {
                var r = m.GetBodyReader();
                r.ReadString();
                r.ReadString();
                return r.ReadString();
            }, OnOwner, emitOnCapturedContext: false, ObserverFlags.None));
            foreach (var signal in new[] { "StatusNotifierHostRegistered", "StatusNotifierHostUnregistered" })
                _watches.Add(await _bus.WatchSignalAsync<string>(null, WatcherPath, Watcher, signal, static (m, _) => m.SenderAsString ?? "", OnHostSignal,
                    ObserverFlags.None, emitOnCapturedContext: false));
            int seen;
            lock (_gate) seen = _owners;
            var current = await OwnerAsync();
            lock (_gate)
                if (_owners == seen) _owner = current;
        }
        catch (Exception)
        {
            Tell(Interlocked.Increment(ref _asked), false);   // no bus: no host, ever
            return;
        }
        await RefreshAsync();
    }

    void OnOwner(Notification<string> n)
    {
        if (!n.HasValue) return;
        lock (_gate)
        {
            _owner = n.Value.Length > 0 ? n.Value : null;
            _owners++;
        }
        _ = RefreshAsync();
    }

    void OnHostSignal(Notification<string> n)
    {
        if (!n.HasValue) return;
        lock (_gate)
            if (n.Value != _owner) return;
        _ = RefreshAsync();
    }

    async Task<string?> OwnerAsync()
    {
        try { return await _bus!.CallMethodAsync(NameOwner(_bus), static (m, _) => m.GetBodyReader().ReadString()).ConfigureAwait(false); }
        catch (DBusErrorReplyException) { return null; }   // nobody owns it: no watcher
    }

    async Task RefreshAsync()
    {
        var asked = Interlocked.Increment(ref _asked);
        var call = AskAsync();
        // the bus doesn't time a call out: a hung watcher would leave a --tray start hidden with no icon
        Tell(asked, await Task.WhenAny(call, Task.Delay(_wait)).ConfigureAwait(false) == call && call.Result);
    }

    async Task<bool> AskAsync()
    {
        try { return await _bus!.CallMethodAsync(HostRegistered(_bus), static (m, _) => m.GetBodyReader().ReadVariantValue().GetBool()).ConfigureAwait(false); }
        catch (Exception) { return false; }   // no watcher, or it went away
    }

    /// <summary>Only the latest ask's answer counts: an earlier one may arrive after it.</summary>
    void Tell(int asked, bool present)
    {
        lock (_gate)
        {
            if (asked != _asked || _told == present) return;
            _told = present;
            _present = present;
            _changed(present);
        }
    }

    static MessageBuffer NameOwner(DBusConnection bus)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetNameOwner", "s");
        w.WriteString(Watcher);
        return w.CreateMessage();
    }

    static MessageBuffer HostRegistered(DBusConnection bus)
    {
        using var w = bus.GetMessageWriter();
        // a watcher that isn't running is no host: never start one
        w.WriteMethodCallHeader(Watcher, WatcherPath, "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.NoAutoStart);
        w.WriteString(Watcher);
        w.WriteString("IsStatusNotifierHostRegistered");
        return w.CreateMessage();
    }

    public void Dispose()
    {
        foreach (var w in _watches) w.Dispose();
        _bus?.Dispose();
    }
}
