using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SCSKiller.Core.App;
using SCSKiller.Ui.Models;
using SCSKiller.Ui.Platform;
using Tmds.DBus.Protocol;

namespace SCSKiller.Ui;

/// <summary>The notification-area icon as a StatusNotifierItem (Avalonia's TrayIcon over D-Bus): left click opens, the
/// menu is the Windows one. It shows only where a tray host runs (<see cref="TrayHost"/>), and <see cref="Added"/> follows
/// that host. Create and use it on the UI thread.</summary>
sealed class Tray : IDisposable
{
    readonly TrayIcon _icon;
    readonly NativeMenuItem _pause = new("Pause compiling"), _quit = new("Quit");
    readonly TrayHost _host;

    public required Action Open { get; init; }
    /// <summary>The menu's middle item: "Pause compiling" or "Resume", null = nothing to pause (shown disabled).</summary>
    public required Func<string?> PauseLabel { get; init; }
    public required Action PauseOrResume { get; init; }
    public required Action Quit { get; init; }
    public required Func<string> QuitLabel { get; init; }
    public required Action<nint> SessionEnding { get; init; }
    public required Action<nint> SessionEnd { get; init; }
    /// <summary>The first answer whether a tray host runs, then each change, on the UI thread.</summary>
    public Action<bool>? HostChanged { get; set; }

    public Tray(string iconPath)
    {
        var open = new NativeMenuItem("Open SCSKiller");
        var menu = new NativeMenu();
        foreach (var item in new[] { open, _pause, _quit }) item.Click += Clicked;
        foreach (var item in new NativeMenuItemBase[] { open, _pause, new NativeMenuItemSeparator(), _quit }) menu.Items.Add(item);
        menu.NeedsUpdate += (_, _) => Refresh();
        _icon = new TrayIcon { Icon = new WindowIcon(iconPath), ToolTipText = "SCSKiller", Menu = menu };
        _icon.Clicked += Clicked;
        // Avalonia makes the platform icon only for one in the Application's TrayIcon.Icons
        if (Host.Platform.Tray) TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });
        _host = TrayHost.Watch(DBusAddress.Session, present => Dispatcher.UIThread.Post(() => HostChanged?.Invoke(present)));
    }

    /// <summary>Set at each status change, which the menu's labels follow too: a host may not ask before it shows the menu.</summary>
    public string Tip
    {
        set
        {
            _icon.ToolTipText = value;
            Refresh();
        }
    }

    /// <summary>A tray host shows the icon. If not, closing the window must really close: hidden without an icon, the user
    /// couldn't get it back.</summary>
    public bool Added => Host.Platform.Tray && _host.Present;

    /// <summary>The icon (a host's activate, usually its left click) and the menu's first item open the window.</summary>
    void Clicked(object? sender, EventArgs e)
    {
        if (sender == _pause) PauseOrResume();
        else if (sender == _quit) Quit();
        else Open();
    }

    void Refresh()
    {
        var pause = PauseLabel();
        _pause.Header = pause ?? "Pause compiling";
        _pause.IsEnabled = pause != null;
        _quit.Header = QuitLabel();
    }

    public void Dispose()
    {
        if (Application.Current is { } app) TrayIcon.SetIcons(app, null);
        _icon.Dispose();
        _host.Dispose();
    }
}

public partial class App
{
    static partial void WatchTrayHost()
    {
        var stranded = new Stranded(() => tray is { Added: true }, () => Main.IsVisible, () => quit.Quitting, ShowWindow,
            (wait, then) => DispatcherTimer.RunOnce(then, wait));
        tray!.HostChanged = present =>
        {
            (Host.Core as ScsKiller)?.Log?.Report(present ? "A tray host shows the icon" : "No tray host shows the icon, closing the window quits");
            Host.Tray.Shown = tray is { Added: true };
            stranded.HostChanged();
        };
    }
}
