using System.Runtime.InteropServices;
using SCSKiller.Core.App;

namespace SCSKiller.Ui.Platform;

/// <summary>window.json's bounds through Get/SetWindowPlacement: the restored bounds in workspace pixels, so a maximized or
/// minimized window keeps where it restores to, and bounds on a monitor that's gone are moved onto one by Windows.</summary>
static class WindowPlacement
{
    /// <summary>Puts the hidden window at <paramref name="saved"/>'s restored bounds without showing it. Returns its client
    /// size in DIPs, for Avalonia to show it at that size. Null: Windows refused.</summary>
    public static (double Width, double Height)? Restore(nint hwnd, WindowBounds saved)
    {
        var wp = new Placement
        {
            Length = Marshal.SizeOf<Placement>(), ShowCmd = SwHide,
            Normal = new(saved.Left, saved.Top, saved.Left + saved.Width, saved.Top + saved.Height),
        };
        var dpi = GetDpiForWindow(hwnd);
        if (!SetWindowPlacement(hwnd, ref wp)) return null;
        // moved onto a monitor of another scale: WM_DPICHANGED resized it on the way. It is there now, so the bounds stick.
        if (GetDpiForWindow(hwnd) != dpi) SetWindowPlacement(hwnd, ref wp);
        GetClientRect(hwnd, out var client);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        // half a pixel more: Avalonia truncates DIPs × scale back to pixels, and a pixel less would resize the window
        return ((client.Right + 0.5) / scale, (client.Bottom + 0.5) / scale);
    }

    /// <summary>The restored bounds, also while maximized or minimized, and whether it shows maximized. Null: no placement.</summary>
    public static WindowBounds? Read(nint hwnd)
    {
        var wp = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return null;
        var r = wp.Normal;
        return new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, WindowBounds.WasMaximized(wp.ShowCmd, wp.Flags));
    }

    const int SwHide = 0;

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(nint hwnd, ref Placement wp);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(nint hwnd, ref Placement wp);

    [StructLayout(LayoutKind.Sequential)] record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] struct Placement { public int Length, Flags, ShowCmd; public NativePoint Min, Max; public NativeRect Normal; }
}
