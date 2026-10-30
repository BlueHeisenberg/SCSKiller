using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SCSKiller.Core.App;
using SCSKiller.Ui.Platform;

namespace SCSKiller.Ui;

/// <summary>The placement on X11, XWayland's too (<see cref="WindowPlacement"/>). Avalonia reports where a maximized window
/// is, not where it restores to, so the bounds come from the moves and resizes made while it was neither.</summary>
public sealed partial class MainWindow
{
    PixelPoint normalAt;
    Size normalSize;
    bool restoresMaximized;   // the state a minimized window goes back to
    PixelPoint? asked;
    (int X, int Y) offset;
    DispatcherTimer? placing;

    void TrackPlacement()
    {
        normalSize = new(Width, Height);
        var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        settle.Tick += (_, _) => Placed();
        placing = settle;
        Opened += (_, _) => settle.Start();
        PositionChanged += (_, _) => Moved();
        Resized += (_, _) => Moved();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState != WindowState.Minimized) restoresMaximized = WindowState == WindowState.Maximized;
        };
    }

    // while the window manager places the mapped window, Position is off by the frame for a moment
    void Moved()
    {
        if (placing is not { } settle) NoteNormal();
        else if (settle.IsEnabled)
        {
            settle.Stop();
            settle.Start();
        }
    }

    // X11 reports the new state before the new size and position
    void NoteNormal()
    {
        if (WindowState == WindowState.Normal && IsVisible) (normalAt, normalSize) = (Position, ClientSize);
    }

    (double Width, double Height)? RestoreBounds(WindowBounds saved)
    {
        var screens = Screens.All.OrderByDescending(s => s.IsPrimary)
            .Select(s => new WindowPlacement.Screen(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height, s.Scaling)).ToList();
        var (at, width, height) = WindowPlacement.Restore(saved, screens);
        if (at is { } p) (Position, WindowStartupLocation, asked) = (new(p.Left, p.Top), WindowStartupLocation.Manual, new(p.Left, p.Top));
        (normalAt, normalSize) = (Position, new(width, height));
        return (width, height);
    }

    void Placed()
    {
        placing?.Stop();
        placing = null;
        if (WindowState != WindowState.Normal) return;   // a maximized frame says nothing about the position
        NoteNormal();
        if (asked is { } a) offset = WindowPlacement.Offset((a.X, a.Y), (Position.X, Position.Y));
    }

    WindowBounds? ReadBounds() => WindowPlacement.Save(normalAt.X, normalAt.Y, offset, normalSize.Width, normalSize.Height, RenderScaling,
        WindowState == WindowState.Maximized || WindowState == WindowState.Minimized && restoresMaximized);
}
