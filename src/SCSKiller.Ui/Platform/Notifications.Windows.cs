using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Platform;

/// <summary>Windows toasts through the Windows App SDK, as the WinUI app shows them.</summary>
static partial class Notifications
{
    static string? testAumid;

    /// <summary>A test identity's AUMID, set before any window: the installed app keeps the one Velopack set.</summary>
    public static void UseIdentity(InstanceIdentity identity)
    {
        if (identity.Aumid is not { } aumid) return;
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(aumid));
        testAumid = aumid;
    }

    /// <summary>Registers this process for toasts and their clicks (<paramref name="invoked"/>, any thread), before the
    /// start's activation is read: on a toast's relaunch, reading it first ends the process. False: notifications can't
    /// work here, the app still does.</summary>
    public static bool Register(Action<IDictionary<string, string>> invoked)
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, a) => invoked(a.Arguments);
            AppNotificationManager.Default.Register();
            RepairActivator();
            return Available = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    /// <summary>The installed app's toast clicks start this exe while it isn't running, also when an older copy in another
    /// folder registered the activator first. Best effort: a toast still shows without it.</summary>
    static void RepairActivator()
    {
        try
        {
            if (testAumid != null || !Updater.Installed || AppStore.IsPortable || Environment.ProcessPath is not { } exe) return;
            using var classes = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes");
            if (classes != null) ToastActivator.Repair(classes, [ToastActivator.VelopackAumid], exe);
        }
        catch (Exception) { }
    }

    /// <summary>A test identity's registration goes with its process: a click after it quit would start the exe without
    /// its data folder.</summary>
    public static void Unregister()
    {
        if (!Available || testAumid == null) return;
        try { AppNotificationManager.Default.UnregisterAll(); }
        catch (Exception) { }   // best effort at quit
    }

    public static void Show(Toast t)
    {
        if (Available) AppNotificationManager.Default.Show(Build(t));
    }

    public static void Driver(IReadOnlyList<GameState> stale) => Show(Toasts.Driver(Host.Core, stale));

    public static void NewShaders(IReadOnlyList<GameState> games, bool compiling) => Show(Toasts.NewShaders(games, compiling));

    public static void TrayNotice() => Show(Toasts.TrayNotice);

    /// <summary>A toast for a step the user takes in another app, shown over it; gone from the notification center after
    /// a few minutes, when the step is stale.</summary>
    public static void Hint(string title, string text) => Show(Toasts.Hint(title, text));

    /// <summary>The toasts' XML for the screenshots run, named as the WinUI app's.</summary>
    public static IEnumerable<(string File, string Xml)> Samples(IScsKiller core) =>
    [
        ("toast.xml", Build(Toasts.Driver(core, core.Games.Take(1).ToList())).Payload),
        ("toast-new-shaders.xml", Build(Toasts.NewShaders([core.Games[0] with { RecordedSinceWarm = 42 }])).Payload),
        ("toast-new-shaders-games.xml", Build(Toasts.NewShaders(core.Games.Take(3).ToList())).Payload),
        ("toast-new-shaders-compiling.xml", Build(Toasts.NewShaders([core.Games[0] with { RecordedSinceWarm = 42 }], compiling: true)).Payload),
        ("toast-news.xml", Build(Toasts.News(NewsState.Sample)).Payload),
        ("toast-news-more.xml", Build(Toasts.NewsMore(4)).Payload),
        ("toast-compiled-later.xml", Build(Toasts.Ended(core.Games[0].Game.Id, ScsKiller.CompiledNotice(core.Games[0].Game.Name))).Payload),
        ("toast-started-during-fold.xml", Build(Toasts.Ended(core.Games[0].Game.Id, ScsKiller.StartedNotice(core.Games[0].Game.Name))).Payload),
    ];

    static AppNotification Build(Toast t)
    {
        var b = new AppNotificationBuilder();
        foreach (var (key, value) in t.Args) b.AddArgument(key, value);
        b.AddText(t.Title).AddText(t.Text);
        foreach (var button in t.Buttons)
        {
            var a = new AppNotificationButton(button.Label);
            foreach (var (key, value) in button.Args) a.AddArgument(key, value);
            b.AddButton(a);
        }
        var toast = b.BuildNotification();
        if (t.Lifetime is { } life) toast.Expiration = DateTimeOffset.Now + life;
        return toast;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SetCurrentProcessExplicitAppUserModelID(string id);
}
