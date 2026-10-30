using Microsoft.Win32.SafeHandles;
using SCSKiller.Ui.Models;
using Tmds.DBus.Protocol;

namespace SCSKiller.Ui.Platform;

/// <summary>systemd-logind's Manager on the system bus. A Flatpak reaches it only with --system-talk-name=org.freedesktop.login1.</summary>
sealed class Login1 : IShutdownLock, IDisposable
{
    const string Service = "org.freedesktop.login1", ObjectPath = "/org/freedesktop/login1", Manager = "org.freedesktop.login1.Manager";

    readonly DBusConnection _bus;
    readonly Lock _order = new();
    Task _events = Task.CompletedTask;

    public event Action<bool>? PrepareForShutdown;
    public event Action<bool>? PrepareForSleep;

    /// <summary>logind's default until its InhibitDelayMaxUSec is read.</summary>
    public TimeSpan MaxDelay { get; private set; } = TimeSpan.FromSeconds(5);

    Login1(string address) => _bus = new DBusConnection(address);

    /// <summary>Connected and watching its signals. An IOException: no system bus, or no logind on it.</summary>
    public static async Task<Login1> ConnectAsync(string? address = null)
    {
        var login = new Login1(address ?? DBusAddress.System!);
        try
        {
            await login._bus.ConnectAsync().ConfigureAwait(false);
            await login.Watch("PrepareForShutdown", start => login.PrepareForShutdown?.Invoke(start)).ConfigureAwait(false);
            await login.Watch("PrepareForSleep", start => login.PrepareForSleep?.Invoke(start)).ConfigureAwait(false);
            var max = await login.MaxDelayAsync().ConfigureAwait(false);   // UINT64_MAX: no limit
            login.MaxDelay = max >= (ulong)TimeSpan.MaxValue.Ticks / 10 ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)max * 10);
            return login;
        }
        catch (Exception e)
        {
            login.Dispose();
            throw new IOException($"logind isn't reachable: {e.Message}", e);
        }
    }

    // in order, off Tmds' receive loop: a handler that stops the compile mustn't hold up the replies
    ValueTask<IDisposable> Watch(string signal, Action<bool> raise) =>
        _bus.WatchSignalAsync(Service, ObjectPath, Manager, signal, (m, _) => m.GetBodyReader().ReadBool(), n =>
        {
            if (!n.HasValue) return;
            var start = n.Value;
            lock (_order) _events = _events.ContinueWith(_ => raise(start), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }, ObserverFlags.None, emitOnCapturedContext: false, null);

    Task<ulong> MaxDelayAsync() =>
        _bus.CallMethodAsync(Call("org.freedesktop.DBus.Properties", "Get", "ss", Manager, "InhibitDelayMaxUSec"),
            (m, _) => m.GetBodyReader().ReadVariantValue().GetUInt64(), null);

    /// <summary>Inhibit("shutdown:sleep", "SCSKiller", why, "delay"): the lock is the file descriptor logind returns.</summary>
    public Task<IDisposable?> TakeAsync(string why) => InhibitAsync("shutdown:sleep", "delay", why);

    /// <summary>Inhibit(what, "SCSKiller", why, mode): released by disposing the file descriptor. Null: refused.</summary>
    public async Task<IDisposable?> InhibitAsync(string what, string mode, string why)
    {
        try
        {
            return await _bus.CallMethodAsync(Call(Manager, "Inhibit", "ssss", what, "SCSKiller", why, mode),
                (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>(), null).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException) { return null; }   // refused (polkit, or a Flatpak's filtered bus)
    }

    MessageBuffer Call(string iface, string member, string signature, params string[] args)
    {
        using var w = _bus.GetMessageWriter();
        w.WriteMethodCallHeader(Service, ObjectPath, iface, member, signature, MessageFlags.None);
        foreach (var a in args) w.WriteString(a);
        return w.CreateMessage();
    }

    public void Dispose() => _bus.Dispose();
}
