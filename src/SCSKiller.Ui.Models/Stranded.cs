namespace SCSKiller.Ui.Models;

/// <summary>The window is hidden while no tray icon shows and no quit runs: nothing could bring it back, so it shows.
/// <paramref name="after"/> runs its action once the time has passed, on the UI thread.</summary>
public sealed class Stranded(Func<bool> trayAdded, Func<bool> visible, Func<bool> quitting, Action show, Action<TimeSpan, Action> after)
{
    /// <summary>How long a tray host may be gone, the window hidden, before the window comes back: a restarting host
    /// returns within it.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    bool known;

    public static bool Of(bool trayAdded, bool visible, bool quitting) => !trayAdded && !visible && !quitting;

    bool Now => Of(trayAdded(), visible(), quitting());

    /// <summary>A tray host came or went (the first call: whether one runs at all). Reads the state as it is now, not as
    /// the change said: a later change may already be in.</summary>
    public void HostChanged()
    {
        var first = !known;
        known = true;
        if (!Now) return;
        if (first) show();   // a --tray start where no host shows the icon
        else after(Grace, () => { if (Now) show(); });
    }
}
