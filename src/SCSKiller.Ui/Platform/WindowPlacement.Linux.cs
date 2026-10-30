using SCSKiller.Core.App;

namespace SCSKiller.Ui.Platform;

/// <summary>window.json on Linux: the frame's top-left and the client size, in pixels, of the window neither maximized nor
/// minimized. The saved position is asked for wherever its title bar shows on a screen, and stays only where the session
/// honours it (X11, and XWayland under KWin): a compositor that places windows itself puts it elsewhere, and only the size
/// comes back (<see cref="Offset"/>). A size larger than the screen's working area shrinks to it.</summary>
static class WindowPlacement
{
    /// <summary>The title bar's strip that must show on a screen: this high, and <see cref="Grab"/> wide.</summary>
    const int Strip = 32, Grab = 64;

    /// <summary>The most a window manager that honours a position puts the frame off it by: its decorations.</summary>
    const int Slack = 64;

    /// <summary>A screen's working area in pixels, and its scale.</summary>
    public readonly record struct Screen(int X, int Y, int Width, int Height, double Scaling);

    /// <summary>The frame's top-left (null: the window manager places it) and the client size in DIPs. <paramref name="screens"/>:
    /// the primary one first.</summary>
    public static ((int Left, int Top)? At, double Width, double Height) Restore(WindowBounds saved, IReadOnlyList<Screen> screens)
    {
        var showing = screens.FirstOrDefault(s => Overlap(saved.Left, saved.Width, s.X, s.Width) >= Grab && Overlap(saved.Top, Strip, s.Y, s.Height) >= Strip / 2);
        bool shows = showing != default;
        var on = shows ? showing : screens.FirstOrDefault();
        double scale = on.Scaling > 0 ? on.Scaling : 1;
        double width = saved.Width, height = saved.Height;
        if (on.Width > 0) (width, height) = (Math.Min(width, on.Width), Math.Min(height, on.Height));
        return (shows ? (saved.Left, saved.Top) : null, width / scale, height / scale);
    }

    /// <summary>Where the mapped frame is against the position asked for, when the session honoured it: zero, or a window
    /// manager's decorations when it took the position for the client's. (0, 0) when the session placed the window itself.</summary>
    public static (int X, int Y) Offset((int Left, int Top) asked, (int Left, int Top) got)
    {
        var (x, y) = (got.Left - asked.Left, got.Top - asked.Top);
        return Math.Abs(x) <= Slack && Math.Abs(y) <= Slack ? (x, y) : (0, 0);
    }

    /// <summary><paramref name="offset"/> (<see cref="Offset"/>) comes off, so the next start's ask lands where the frame is.</summary>
    public static WindowBounds Save(int left, int top, (int X, int Y) offset, double width, double height, double scaling, bool maximized) =>
        new(left - offset.X, top - offset.Y, (int)Math.Round(width * scaling), (int)Math.Round(height * scaling), maximized);

    static long Overlap(long from, long length, long areaFrom, long areaLength) =>
        Math.Max(0, Math.Min(from + length, areaFrom + areaLength) - Math.Max(from, areaFrom));
}
