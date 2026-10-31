using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Models;
using Tmds.DBus.Protocol;

namespace SCSKiller.Ui.Platform;

/// <summary>Desktop notifications over D-Bus (<see cref="DesktopNotifier"/>), worded and clicked as the Windows toasts.
/// A click reaches only the running app: nothing starts it for one.</summary>
static partial class Notifications
{
    /// <summary>How long the start waits for the session's notification service, which may start on the first call.</summary>
    static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(2);
    static DesktopNotifier? notifier;
    static bool unregistered;

    public static void UseIdentity(InstanceIdentity identity) { }

    /// <summary>Connects before the start's driver notification. False: no service answered, the app works without.</summary>
    public static bool Register(Action<IDictionary<string, string>> invoked)
    {
        var ok = Connect(invoked);
        if (!ok) (Host.Core as ScsKiller)?.Log?.Report($"Notifications aren't available: {Error}");
        return ok;
    }

    static bool Connect(Action<IDictionary<string, string>> invoked)
    {
        if (DBusAddress.Session is not { } address)
        {
            Error = "no D-Bus session bus";
            return false;
        }
        var connect = Task.Run(() => DesktopNotifier.ConnectAsync(address, DesktopNotifier.InFlatpak, IconUri(), a => invoked(new Dictionary<string, string>(a))));
        try
        {
            if (!connect.Wait(ConnectWait))
            {
                Error = "the notification service didn't answer";
                _ = connect.ContinueWith(t =>
                {
                    if (!t.IsCompletedSuccessfully)
                    {
                        _ = t.Exception;   // observed: an unobserved one is logged as a crash
                        return;
                    }
                    // a service the bus had to start (the portal on a Flatpak's first start) can answer later
                    if (!Host.Ui.TryEnqueue(() => Late(t.Result))) t.Result.Dispose();
                });
                return false;
            }
            notifier = connect.Result;
            return Available = true;
        }
        catch (AggregateException e)
        {
            Error = e.InnerException?.Message ?? e.Message;
            return false;
        }
    }

    static void Late(DesktopNotifier n)
    {
        if (unregistered || notifier != null)
        {
            n.Dispose();
            return;
        }
        notifier = n;
        Available = true;
        (Host.Core as ScsKiller)?.Log?.Report("Notifications are available: the notification service answered late");
    }

    static string IconUri()
    {
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        return File.Exists(ico) ? new Uri(ico).AbsoluteUri : "";
    }

    /// <summary>At quit: the open notifications go, since their clicks can't reach the app anymore.</summary>
    public static void Unregister()
    {
        unregistered = true;
        if (notifier is not { } n) return;
        notifier = null;
        n.Withdraw(TimeSpan.FromSeconds(1));
        n.Dispose();
    }

    public static void Show(Toast t)
    {
        if (notifier is { } n) _ = n.ShowAsync(t);
    }

    public static void Driver(IReadOnlyList<GameState> stale) => Show(Toasts.Driver(Host.Core, stale));
    public static void NewShaders(IReadOnlyList<GameState> games, bool compiling) => Show(Toasts.NewShaders(games, compiling));
    public static void TrayNotice() => Show(Toasts.TrayNotice);
    public static void Hint(string title, string text) => Show(Toasts.Hint(title, text));

    /// <summary>What the notifications show, for the screenshots run, named as the WinUI app's toasts.</summary>
    public static IEnumerable<(string File, string Xml)> Samples(IScsKiller core) =>
    [
        ("toast.txt", DesktopNotifier.Describe(Toasts.Driver(core, core.Games.Take(1).ToList()))),
        ("toast-new-shaders.txt", DesktopNotifier.Describe(Toasts.NewShaders([core.Games[0] with { RecordedSinceWarm = 42 }]))),
        ("toast-new-shaders-games.txt", DesktopNotifier.Describe(Toasts.NewShaders(core.Games.Take(3).ToList()))),
        ("toast-new-shaders-compiling.txt", DesktopNotifier.Describe(Toasts.NewShaders([core.Games[0] with { RecordedSinceWarm = 42 }], compiling: true))),
        ("toast-news.txt", DesktopNotifier.Describe(Toasts.News(NewsState.Sample))),
        ("toast-news-more.txt", DesktopNotifier.Describe(Toasts.NewsMore(4))),
        ("toast-compiled-later.txt", DesktopNotifier.Describe(Toasts.Ended(core.Games[0].Game.Id, ScsKiller.CompiledNotice(core.Games[0].Game.Name)))),
        ("toast-started-during-fold.txt", DesktopNotifier.Describe(Toasts.Ended(core.Games[0].Game.Id, ScsKiller.StartedNotice(core.Games[0].Game.Name)))),
    ];
}
