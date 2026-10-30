using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Platform;

/// <summary>One instance per key and user session through the Windows App SDK's AppInstance, as the WinUI app.</summary>
static partial class SingleInstance
{
    static Mutex? folder;
    // the payload, read in the event: the activation is a COM object of the redirecting process, gone once it exits
    sealed record Redirect(IDictionary<string, string>? Toast, string Line);
    static readonly List<Redirect> early = [];
    static Action<Redirect>? handle;

    /// <summary>This process holds the key and, across this app's exes (Windows scopes the key to one exe's path), its data
    /// folder's mutex: the only instance of this app on that folder.</summary>
    public static bool Held => folder != null;

    /// <summary>True: another instance holds the identity's key and now has this launch's activation, so this one exits.
    /// <paramref name="toastStart"/>: a toast click started it.</summary>
    public static bool RedirectedToRunning(InstanceIdentity identity, bool toastStart)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var found = AppInstance.FindOrRegisterForKey(identity.Key);
        if (found.IsCurrent)
        {
            // the SDK doesn't keep a redirect nobody listens to yet: kept here until Listen
            AppInstance.GetCurrent().Activated += (_, a) =>
            {
                var r = a.Kind == ExtendedActivationKind.AppNotification
                    ? new Redirect(new Dictionary<string, string>(((AppNotificationActivatedEventArgs)a.Data).Arguments), "")
                    : new Redirect(null, (a.Data as Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs)?.Arguments ?? "");
                Action<Redirect>? h;
                lock (early)
                    if ((h = handle) == null) early.Add(r);
                h?.Invoke(r);
            };
            var m = new Mutex(true, @"Local\SCSKiller.Folder." + identity.Folder, out var created);
            if (created) folder = m;
            else m.Dispose();
            return false;
        }
        AllowSetForegroundWindow(found.ProcessId);   // its window may come to the front: this launch has the user's click
        // a toast's activation can be read only once this process registered for toasts, else the SDK ends the process
        if (toastStart) AppNotificationManager.Default.Register();
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var done = new ManualResetEvent(false);
        // On another thread: awaiting the redirect on this STA thread hangs. WaitOne pumps COM meanwhile.
        Task.Run(async () =>
        {
            try { await found.RedirectActivationToAsync(activation); }
            finally { done.Set(); }
        });
        done.WaitOne(TimeSpan.FromSeconds(30));
        return true;
    }

    /// <summary>The toast's arguments when a toast click started this process, else null.</summary>
    public static IDictionary<string, string>? LaunchToast() =>
        AppInstance.GetCurrent().GetActivatedEventArgs() is { Kind: ExtendedActivationKind.AppNotification } a ? ((AppNotificationActivatedEventArgs)a.Data).Arguments : null;

    /// <summary>Another launch handed its activation over (any thread), also one that came before this call: its command
    /// line, or a toast's arguments.</summary>
    public static void Listen(Action<string> launched, Action<IDictionary<string, string>> toast)
    {
        void Handle(Redirect r)
        {
            if (r.Toast != null) toast(r.Toast);
            else launched(r.Line);
        }
        List<Redirect> before;
        lock (early)
        {
            handle = Handle;
            before = [.. early];
            early.Clear();
        }
        foreach (var a in before) Handle(a);
    }

    /// <summary>A redirected launch allowed this process to take the foreground (<see cref="RedirectedToRunning"/>).</summary>
    public static void ToFront(Avalonia.Controls.Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is { } hwnd) SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
}
