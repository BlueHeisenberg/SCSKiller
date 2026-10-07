using System.Runtime.InteropServices;
using SCSKiller.Core.App;

namespace SCSKiller.App;

/// <summary>The notification-area icon (Shell_NotifyIcon) on a hidden top-level window, which also receives the session's
/// end (WM_QUERYENDSESSION / WM_ENDSESSION: WinUI doesn't surface them). A regular hidden window, not a message-only one:
/// only top-level windows get the TaskbarCreated broadcast (the icon comes back after Explorer restarts) and the
/// end-session messages. Create and use it on the UI thread.</summary>
sealed class Tray : IDisposable
{
    const int CallbackMessage = 0x8000 + 1;   // WM_APP + 1
    const uint IdOpen = 1, IdPause = 2, IdQuit = 3;

    static readonly WndProc Proc = Dispatch;   // kept alive: Windows calls it for the process's lifetime
    static readonly Dictionary<nint, Tray> ByWindow = [];
    static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    readonly nint _hwnd, _icon;
    string _tip = "SCSKiller";

    /// <summary>Left-click or double-click.</summary>
    public required Action Open { get; init; }
    /// <summary>The menu's middle item: "Pause compiling" or "Resume", null = nothing to pause (shown disabled).</summary>
    public required Func<string?> PauseLabel { get; init; }
    public required Action PauseOrResume { get; init; }
    public required Action Quit { get; init; }
    public required Func<string> QuitLabel { get; init; }
    /// <summary>Windows is about to end the session (shutdown, restart, sign-out): start stopping.</summary>
    public required Action<nint> SessionEnding { get; init; }
    /// <summary>The session ends once this returns: finish stopping (blocking, bounded).</summary>
    public required Action<nint> SessionEnd { get; init; }

    public Tray(string iconPath)
    {
        var wc = new WndClassEx { Size = Marshal.SizeOf<WndClassEx>(), WndProc = Marshal.GetFunctionPointerForDelegate(Proc), Instance = GetModuleHandleW(null), ClassName = "SCSKiller.Tray" };
        RegisterClassExW(ref wc);   // fails harmlessly if already registered
        _hwnd = CreateWindowExW(0, "SCSKiller.Tray", "SCSKiller", 0, 0, 0, 0, 0, 0, 0, wc.Instance, 0);
        ByWindow[_hwnd] = this;
        _icon = LoadImageW(0, iconPath, 1 /* IMAGE_ICON */, GetSystemMetrics(49 /* SM_CXSMICON */), GetSystemMetrics(50), 0x10 /* LR_LOADFROMFILE */);
        Add();
    }

    /// <summary>The tooltip (at most 127 characters).</summary>
    public string Tip
    {
        set
        {
            _tip = value.Length > 127 ? value[..127] : value;
            var d = Data(0x4 /* NIF_TIP */);
            Shell_NotifyIconW(1 /* NIM_MODIFY */, ref d);
        }
    }

    /// <summary>The icon is in the notification area. If not (the shell refused it), closing the window must really close:
    /// hidden without an icon, the user couldn't get it back.</summary>
    public bool Added { get; private set; }

    void Add()
    {
        var d = Data(0x1 | 0x2 | 0x4 /* NIF_MESSAGE | NIF_ICON | NIF_TIP */);
        Added = Shell_NotifyIconW(0 /* NIM_ADD */, ref d);
    }

    NotifyIconData Data(uint flags) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(), Window = _hwnd, Id = 1, Flags = flags, CallbackMessage = CallbackMessage, Icon = _icon, Tip = _tip,
    };

    void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0 /* MF_STRING */, IdOpen, Loc.Text("Open SCSKiller"));
        var pause = PauseLabel();
        AppendMenuW(menu, pause == null ? 0x1u /* MF_GRAYED */ : 0, IdPause, pause ?? Loc.Text("Pause compiling"));
        AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenuW(menu, 0, IdQuit, QuitLabel());
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);   // else the menu doesn't close when the user clicks elsewhere
        var id = TrackPopupMenu(menu, 0x100 | 0x2 | 0x80 /* TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY */, pt.X, pt.Y, 0, _hwnd, 0);
        PostMessageW(_hwnd, 0, 0, 0);
        DestroyMenu(menu);
        switch ((uint)id)
        {
            case IdOpen: Open(); break;
            case IdPause: PauseOrResume(); break;
            case IdQuit: Quit(); break;
        }
    }

    static nint Dispatch(nint hwnd, uint msg, nint w, nint l)
    {
        if (ByWindow.TryGetValue(hwnd, out var t))
            switch (msg)
            {
                case CallbackMessage:
                    switch ((uint)l & 0xFFFF)
                    {
                        case 0x202 or 0x203: t.Open(); break;       // WM_LBUTTONUP, WM_LBUTTONDBLCLK
                        case 0x205: t.ShowMenu(); break;            // WM_RBUTTONUP
                    }
                    return 0;
                case 0x11:   // WM_QUERYENDSESSION: allow it, and start stopping
                    t.SessionEnding(hwnd);
                    return 1;
                case 0x16:   // WM_ENDSESSION
                    if (w != 0) t.SessionEnd(hwnd);
                    return 0;
                default:
                    if (msg == TaskbarCreated) t.Add();   // Explorer restarted
                    break;
            }
        return DefWindowProcW(hwnd, msg, w, l);
    }

    public void Dispose()
    {
        var d = Data(0);
        Shell_NotifyIconW(2 /* NIM_DELETE */, ref d);
        ByWindow.Remove(_hwnd);
        DestroyWindow(_hwnd);
        if (_icon != 0) DestroyIcon(_icon);
    }

    delegate nint WndProc(nint hwnd, uint msg, nint w, nint l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClassEx
    {
        public int Size;
        public uint Style;
        public nint WndProc;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    struct Point { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint LoadImageW(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool PostMessageW(nint hwnd, uint msg, nint w, nint l);
}
