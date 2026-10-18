namespace SCSKiller.Core.App;

public enum TrayAction { None, Open, Menu }

/// <summary>What the notification-area icon does for each mouse message Windows sends it (legacy callback, no
/// NOTIFYICON_VERSION_4): a left click opens SCSKiller, a right click shows the menu.</summary>
public sealed class TrayClicks(int doubleClickMs)
{
    long lastOpen = long.MinValue / 2;

    /// <param name="now">milliseconds, any steady clock</param>
    public TrayAction On(uint message, long now)
    {
        switch (message)
        {
            // a double click is button-up, WM_LBUTTONDBLCLK, button-up: one open. WM_LBUTTONDBLCLK without a click just
            // before (keyboard selection on the legacy callback) still opens
            case 0x202 or 0x203:   // WM_LBUTTONUP, WM_LBUTTONDBLCLK
                if (now - lastOpen <= doubleClickMs) return TrayAction.None;
                lastOpen = now;
                return TrayAction.Open;
            case 0x205: return TrayAction.Menu;   // WM_RBUTTONUP
            default: return TrayAction.None;
        }
    }
}
