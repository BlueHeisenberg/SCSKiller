using System.Globalization;
using SCSKiller.Ui.Models;
using Tmds.DBus.Protocol;

namespace SCSKiller.Ui.Platform;

/// <summary>Notifications on a Linux desktop over D-Bus: org.freedesktop.Notifications, or inside a Flatpak the notification
/// portal. A click reaches the app only through this connection, so nothing starts a quit app for one: the open ones are
/// withdrawn at quit. Only the service that showed a notification can click it. Thread-safe.</summary>
sealed class DesktopNotifier : IDisposable
{
    const string Fdo = "org.freedesktop.Notifications", FdoPath = "/org/freedesktop/Notifications";
    const string Portal = "org.freedesktop.portal.Desktop", PortalPath = "/org/freedesktop/portal/desktop", PortalNotification = "org.freedesktop.portal.Notification";
    const string AppName = "SCSKiller", DefaultAction = "default", DefaultLabel = "Open SCSKiller";
    /// <summary>Notifications remembered for their clicks: the oldest beyond it is forgotten.</summary>
    internal const int MaxOpen = 64;

    readonly DBusConnection _bus;
    readonly bool _portal, _markup;
    readonly string _icon;
    readonly Action<IReadOnlyList<KeyValuePair<string, string>>> _invoked;
    readonly Dictionary<string, Shown> _open = [];
    readonly List<IDisposable> _watches = [];
    long _shown;

    /// <param name="Server">The unique bus name of the service that took it: only its signals are its clicks, and its
    /// close goes there.</param>
    sealed record Shown(Toast Toast, string Server, long Order);

    /// <summary>Inside a Flatpak only the portal reaches the desktop's notifications.</summary>
    public static bool InFlatpak => File.Exists("/.flatpak-info");

    DesktopNotifier(DBusConnection bus, bool portal, bool markup, string icon, Action<IReadOnlyList<KeyValuePair<string, string>>> invoked) =>
        (_bus, _portal, _markup, _icon, _invoked) = (bus, portal, markup, icon, invoked);

    /// <summary>Connects to the bus at <paramref name="address"/> and checks that a notification service answers.
    /// <paramref name="icon"/>: the app's icon as a file URI, or empty. <paramref name="invoked"/> gets a click's arguments
    /// (a button's, or the body's), on another thread. Throws when no service answers.</summary>
    public static async Task<DesktopNotifier> ConnectAsync(string address, bool portal, string icon, Action<IReadOnlyList<KeyValuePair<string, string>>> invoked)
    {
        var bus = new DBusConnection(address);
        try
        {
            await bus.ConnectAsync();
            var markup = false;
            if (portal) await bus.CallMethodAsync(PortalVersion(bus), static (m, _) => m.GetBodyReader().ReadVariantValue().GetUInt32());
            else markup = (await bus.CallMethodAsync(Capabilities(bus), static (m, _) => m.GetBodyReader().ReadArrayOfString())).Contains("body-markup");
            var n = new DesktopNotifier(bus, portal, markup, icon, invoked);
            await n.WatchAsync();
            return n;
        }
        catch
        {
            bus.Dispose();
            throw;
        }
    }

    /// <param name="Action">The clicked action, null for a notification the service closed.</param>
    readonly record struct Signal(string Sender, string Id, string? Action);

    // any client can send these: a body the reader can't read would end the connection, so a wrong signature is ignored
    async Task WatchAsync()
    {
        if (_portal)
            await WatchAsync(PortalPath, PortalNotification, "ActionInvoked", static (m, _) =>
            {
                if (m.SignatureAsString != "ssav") return null;
                var r = m.GetBodyReader();
                return new Signal(m.SenderAsString ?? "", r.ReadString(), r.ReadString());
            });
        else
        {
            await WatchAsync(FdoPath, Fdo, "ActionInvoked", static (m, _) =>
            {
                if (m.SignatureAsString != "us") return null;
                var r = m.GetBodyReader();
                return new Signal(m.SenderAsString ?? "", Key(r.ReadUInt32()), r.ReadString());
            });
            await WatchAsync(FdoPath, Fdo, "NotificationClosed", static (m, _) =>
                m.SignatureAsString != "uu" ? null : new Signal(m.SenderAsString ?? "", Key(m.GetBodyReader().ReadUInt32()), null));
        }
    }

    async Task WatchAsync(string path, string iface, string member, MessageValueReader<Signal?> read) =>
        _watches.Add(await _bus.WatchSignalAsync<Signal?>(null, path, iface, member, read, OnSignal, ObserverFlags.None, emitOnCapturedContext: false));

    static string Key(uint id) => id.ToString(CultureInfo.InvariantCulture);

    void OnSignal(Notification<Signal?> n)
    {
        if (!n.HasValue || n.Value is not { } s) return;
        if (s.Action is { } action) Clicked(s.Sender, s.Id, action);
        else Forget(s.Sender, s.Id);
    }

    void Clicked(string sender, string id, string action)
    {
        Toast toast;
        lock (_open)
        {
            if (!_open.TryGetValue(id, out var s) || s.Server != sender) return;
            toast = s.Toast;
            if (_portal) _open.Remove(id);   // the portal says nothing when it drops one: a clicked one is gone
        }
        if (action == DefaultAction) _invoked(toast.Args);
        else if (action.StartsWith('b') && int.TryParse(action.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < toast.Buttons.Count)
            _invoked(toast.Buttons[i].Args);
    }

    void Forget(string sender, string id)
    {
        lock (_open)
            if (_open.TryGetValue(id, out var s) && s.Server == sender) _open.Remove(id);
    }

    /// <summary>Shows <paramref name="t"/>; a failure (the service went away) shows nothing.</summary>
    public async Task ShowAsync(Toast t)
    {
        try
        {
            var order = Interlocked.Increment(ref _shown);
            string id, server;
            if (_portal)
            {
                id = $"scskiller-{Environment.ProcessId}-{order}";
                server = await _bus.CallMethodAsync(AddNotification(id, t), static (m, _) => m.SenderAsString ?? "").ConfigureAwait(false);
            }
            else
            {
                (var n, server) = await _bus.CallMethodAsync(Notify(t), static (m, _) => (m.GetBodyReader().ReadUInt32(), m.SenderAsString ?? "")).ConfigureAwait(false);
                id = Key(n);
            }
            var shown = new Shown(t, server, order);
            KeyValuePair<string, Shown>? evicted = null;
            lock (_open)
            {
                _open[id] = shown;
                if (_open.Count > MaxOpen)
                {
                    evicted = _open.MinBy(e => e.Value.Order);
                    _open.Remove(evicted.Value.Key);
                }
            }
            // forgotten, its buttons would do nothing: it goes from the screen too
            if (evicted is { Key: var oldId, Value: var old }) await SendCloseAsync(oldId, old).ConfigureAwait(false);
            if (t.Lifetime is { } life)
            {
                await Task.Delay(life).ConfigureAwait(false);
                await CloseAsync(id, shown).ConfigureAwait(false);
            }
        }
        catch (Exception) { }   // best effort, as a toast the OS refuses: the app works on
    }

    /// <param name="only">Close it only if the id is still this one's: a restarted service gives its ids again.</param>
    async Task CloseAsync(string id, Shown? only = null)
    {
        Shown? s;
        lock (_open)
            if (!_open.TryGetValue(id, out s) || (only != null && s != only) || !_open.Remove(id)) return;
        await SendCloseAsync(id, s).ConfigureAwait(false);
    }

    /// <summary>To the service that showed it, by its unique name: a restarted one under the well-known name gives the same
    /// id to another app's notification.</summary>
    async Task SendCloseAsync(string id, Shown s)
    {
        try
        {
            if (_portal) await _bus.CallMethodAsync(RemoveNotification(s.Server, id)).ConfigureAwait(false);
            else await _bus.CallMethodAsync(CloseNotification(s.Server, uint.Parse(id, CultureInfo.InvariantCulture))).ConfigureAwait(false);
        }
        catch (Exception) { }   // already gone, or its service is
    }

    /// <summary>Withdraws every open notification, waiting at most <paramref name="max"/>: once the app quits, their
    /// buttons would do nothing.</summary>
    public void Withdraw(TimeSpan max)
    {
        string[] ids;
        lock (_open) ids = [.. _open.Keys];
        // off the caller's context: the UI thread waits here, so it can't run the closes' continuations
        if (ids.Length > 0) Task.Run(() => Task.WhenAll(ids.Select(id => CloseAsync(id)))).Wait(max);
    }

    public void Dispose()
    {
        foreach (var w in _watches) w.Dispose();
        _bus.Dispose();
    }

    /// <summary>What a notification shows, for the screenshots run.</summary>
    public static string Describe(Toast t) =>
        $"{t.Title}\n{t.Text}\n" + string.Concat(t.Buttons.Select(b => $"[{b.Label}] ")).TrimEnd() + "\n";

    /// <summary>The spec's body markup is a subset of XML: only these three are special.</summary>
    internal static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    static MessageBuffer Capabilities(DBusConnection bus)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(Fdo, FdoPath, Fdo, "GetCapabilities");
        return w.CreateMessage();
    }

    static MessageBuffer PortalVersion(DBusConnection bus)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(Portal, PortalPath, "org.freedesktop.DBus.Properties", "Get", "ss");
        w.WriteString(PortalNotification);
        w.WriteString("version");
        return w.CreateMessage();
    }

    MessageBuffer Notify(Toast t)
    {
        using var w = _bus.GetMessageWriter();
        w.WriteMethodCallHeader(Fdo, FdoPath, Fdo, "Notify", "susssasa{sv}i");
        w.WriteString(AppName);
        w.WriteUInt32(0);
        w.WriteString(_icon);
        w.WriteString(t.Title);
        w.WriteString(_markup ? Escape(t.Text) : t.Text);
        string[] actions = [DefaultAction, DefaultLabel, .. t.Buttons.SelectMany((b, i) => new[] { $"b{i}", b.Label })];
        w.WriteArray(actions);
        w.WriteDictionary(new Dictionary<string, VariantValue>());
        w.WriteInt32(-1);
        return w.CreateMessage();
    }

    MessageBuffer CloseNotification(string server, uint id)
    {
        using var w = _bus.GetMessageWriter();
        w.WriteMethodCallHeader(server, FdoPath, Fdo, "CloseNotification", "u");
        w.WriteUInt32(id);
        return w.CreateMessage();
    }

    MessageBuffer AddNotification(string id, Toast t)
    {
        using var w = _bus.GetMessageWriter();
        w.WriteMethodCallHeader(Portal, PortalPath, PortalNotification, "AddNotification", "sa{sv}");
        w.WriteString(id);
        var dict = w.WriteDictionaryStart();
        w.WriteDictionaryEntryStart();
        w.WriteString("title");
        w.WriteVariantString(t.Title);
        w.WriteDictionaryEntryStart();
        w.WriteString("body");
        w.WriteVariantString(t.Text);
        w.WriteDictionaryEntryStart();
        w.WriteString("default-action");
        w.WriteVariantString(DefaultAction);
        if (t.Buttons.Count > 0)
        {
            w.WriteDictionaryEntryStart();
            w.WriteString("buttons");
            w.WriteSignature("aa{sv}");
            var buttons = w.WriteArrayStart(DBusType.Array);
            for (var i = 0; i < t.Buttons.Count; i++)
            {
                var button = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart();
                w.WriteString("label");
                w.WriteVariantString(t.Buttons[i].Label);
                w.WriteDictionaryEntryStart();
                w.WriteString("action");
                w.WriteVariantString($"b{i}");
                w.WriteDictionaryEnd(button);
            }
            w.WriteArrayEnd(buttons);
        }
        w.WriteDictionaryEnd(dict);
        return w.CreateMessage();
    }

    MessageBuffer RemoveNotification(string server, string id)
    {
        using var w = _bus.GetMessageWriter();
        w.WriteMethodCallHeader(server, PortalPath, PortalNotification, "RemoveNotification", "s");
        w.WriteString(id);
        return w.CreateMessage();
    }
}
