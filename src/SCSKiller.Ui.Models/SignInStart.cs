using SCSKiller.Core.App;

namespace SCSKiller.Ui.Models;

/// <summary>The start at sign-in (<see cref="WindowsStartup.TrayArg"/>, the Run value or the autostart entry): no window,
/// nobody asked for one.</summary>
public static class SignInStart
{
    public enum Pass { None, Scan, ScanThenDriverCheck }

    public static bool Is(IReadOnlyList<string> args) => args.Contains(WindowsStartup.TrayArg);

    /// <summary>What it runs before anything waits on the user, the window hidden: the scan, whose first step takes out what a
    /// crashed risk or offline session left (Linux has no RunOnce) and which the watcher's recorder pass waits for. On Linux
    /// the driver-update check follows it: the timer's comes only after Settings sets a mode, and a Flatpak has none. None for any other start.</summary>
    public static Pass For(IReadOnlyList<string> args, bool windows) => !Is(args) ? Pass.None : windows ? Pass.Scan : Pass.ScanThenDriverCheck;

    /// <summary>An alpha whose gate is closed (<see cref="AlphaGate"/>) quits at sign-in, its gate window unopened: a refused
    /// alpha runs nothing, and nobody is waiting for the window.</summary>
    public static bool Quits(IReadOnlyList<string> args, bool gateNeeded, Func<bool> gatePassed) => Is(args) && gateNeeded && !gatePassed();
}
