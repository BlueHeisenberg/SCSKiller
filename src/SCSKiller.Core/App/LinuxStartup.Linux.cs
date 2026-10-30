using System.Text;

namespace SCSKiller.Core.App;

/// <summary>"Start when you sign in" on Linux (<see cref="Settings.StartWithWindows"/>): an XDG autostart entry that starts
/// the app with <see cref="WindowsStartup.TrayArg"/>, or in a Flatpak the Background portal's autostart. Copies share the
/// one entry as they share the Run value on Windows: a copy changes it only when it starts that copy, or when the user turns
/// the setting on or off there. An entry of the same name that SCSKiller didn't write is never changed or removed.</summary>
public static class LinuxStartup
{
    public const string AppId = MenuEntry.AppId, FileName = AppId + ".desktop";
    // the key that makes an entry ours: a desktop's autostart settings copy the menu entry under this same name
    const string OwnKey = "X-SCSKiller-Autostart";

    /// <summary>$XDG_CONFIG_HOME, else ~/.config: a relative one is ignored, as the XDG spec says.</summary>
    public static string ConfigHome(Func<string, string?> env) =>
        env("XDG_CONFIG_HOME") is { Length: > 0 } c && Path.IsPathFullyQualified(c) ? c
            : Path.Combine(env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    public static string EntryFile(Func<string, string?> env) => Path.Combine(ConfigHome(env), "autostart", FileName);

    /// <summary>Flatpak's sandbox, with our app in it: another sandboxed app (a Flatpak Steam) that starts this copy has the
    /// first two too.</summary>
    public static bool InFlatpak => File.Exists("/.flatpak-info") && Environment.GetEnvironmentVariable("FLATPAK_ID") is { Length: > 0 }
        && AppContext.BaseDirectory.StartsWith("/app/", StringComparison.Ordinal);

    /// <summary>What an entry starts this copy with (<see cref="AppImage.Program"/>), or null with why it can't start at sign-in.</summary>
    public static string? ThisProgram(out string? refused) =>
        Target(Environment.ProcessPath, Environment.GetEnvironmentVariable, Libc.RealPath, Libc.UserId, Libc.Lstat, Libc.AccessAcl, out refused);

    /// <summary><see cref="AppImage.Program"/> when the menu entry's guard (<see cref="MenuEntry.Refused"/>) lets it start, else
    /// null with why: never in an AppImage's mount, and never where another user could replace it.</summary>
    internal static string? Target(string? processPath, Func<string, string?> env, Func<string, string?> realPath, uint uid,
        Func<string, (uint Uid, uint Mode)?> lstat, Func<string, int> acl, out string? refused)
    {
        if (AppImage.Program(processPath, env, realPath) is not { } program)
        {
            refused = "this process's file isn't known";
            return null;
        }
        refused = MenuEntry.Refused(program, env, uid, lstat, acl);
        return refused == null ? program : null;
    }

    // what a desktop's autostart settings write to turn an entry off
    const string Hidden = "Hidden", GnomeEnabled = "X-GNOME-Autostart-enabled";

    /// <summary>The entry that starts <paramref name="program"/> hidden in the tray, as the Run value does on Windows.
    /// TryExec makes a desktop skip it once the program is gone. <paramref name="was"/>: the keys of the entry it replaces,
    /// whose Hidden and X-GNOME-Autostart-enabled, the desktop's off switch, it keeps.</summary>
    public static string Entry(string program, IReadOnlyDictionary<string, string>? was = null) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=SCSKiller
        Comment=Compiles your games' shaders into your GPU driver's cache before you play
        Exec={MenuEntry.ExecArg(program)} {WindowsStartup.TrayArg}
        TryExec={program}
        Icon={AppId}
        Terminal=false
        {GnomeEnabled}={was?.GetValueOrDefault(GnomeEnabled) ?? "true"}
        {(was?.GetValueOrDefault(Hidden) is { } hidden ? $"{Hidden}={hidden}\n" : "")}{OwnKey}=true

        """;

    /// <summary>An entry file as read: its [Desktop Entry] keys, whether SCSKiller wrote it, the program its TryExec names,
    /// and whether the desktop's autostart settings left it on.</summary>
    internal sealed record Found(string Text, IReadOnlyDictionary<string, string> Keys)
    {
        public bool Ours => Keys.GetValueOrDefault(OwnKey) == "true";
        public string? Program => Keys.TryGetValue("TryExec", out var p) ? Unescape(p) : null;
        public bool Enabled => Keys.GetValueOrDefault(Hidden) != "true" && Keys.GetValueOrDefault(GnomeEnabled) != "false";
        public bool Starts(string program) => Ours && Program == program;
    }

    /// <summary>Null when there is no file. One that can't be read throws, and a link to nothing is another's: neither is
    /// ever written over.</summary>
    internal static Found? Read(string file)
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new FileInfo(file).LinkTarget == null ? null : new("", new Dictionary<string, string>());
        }
        var keys = new Dictionary<string, string>();
        var inEntry = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) inEntry = line == "[Desktop Entry]";
            else if (inEntry && !line.StartsWith('#') && line.IndexOf('=') is > 0 and var eq) keys.TryAdd(line[..eq].TrimEnd(), line[(eq + 1)..].TrimStart());
        }
        return new(text, keys);
    }

    static string Unescape(string value)
    {
        var s = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
            s.Append(value[i] == '\\' && i + 1 < value.Length ? value[++i] switch { 's' => ' ', 'n' => '\n', 't' => '\t', 'r' => '\r', var c => c } : value[i]);
        return s.ToString();
    }

    /// <summary>This copy starts at sign-in: its own entry is there and the desktop's autostart settings left it on. What the
    /// Settings checkbox shows: off when the entry can't be read.</summary>
    public static bool IsOn(string program, string file)
    {
        try { return Read(file) is { Enabled: true } f && f.Starts(program); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>The user turned the setting on or off in this copy: on writes this copy's entry over a missing one, ours or
    /// another copy's (and turns one the desktop's settings turned off back on); off removes the entry only if it starts
    /// <paramref name="program"/>. Another's entry of the same name is never touched. True if it changed.</summary>
    public static bool Set(bool on, string program, string file)
    {
        var now = Read(file);
        if (now is { Ours: false }) return false;
        if (on) return MenuEntry.Write(file, now?.Text, Entry(program));
        if (now == null || !now.Starts(program)) return false;
        File.Delete(file);
        return true;
    }

    /// <summary>App start: this copy's entry follows the setting, unless the desktop's autostart settings turned it off;
    /// another copy's is never touched while its program exists. An <paramref name="installed"/> copy whose setting is on
    /// writes its own over another copy's whose program is gone (<paramref name="exists"/>: an AppImage moved), keeping
    /// the desktop's off switch. A missing
    /// entry stays missing: only the user's toggle adds one (the setting's default is on, and a desktop removes entries).</summary>
    public static bool Reconcile(bool on, string program, bool installed, string file, Func<string, bool> exists)
    {
        var now = Read(file);
        if (now is not { Ours: true }) return false;
        if (now.Starts(program))
        {
            if (on) return now.Enabled && MenuEntry.Write(file, now.Text, Entry(program, now.Keys));
            File.Delete(file);
            return true;
        }
        return on && installed && (now.Program is not { } other || !exists(other)) && MenuEntry.Write(file, now.Text, Entry(program, now.Keys));
    }

    /// <summary>The app's call, as <see cref="WindowsStartup.Apply"/>: <paramref name="byUser"/> is <see cref="Set"/>, otherwise
    /// the start's <see cref="Reconcile"/>. Only a <paramref name="release"/> build, and only with a <paramref name="program"/>
    /// that may start at sign-in (<see cref="ThisProgram"/>). A folder that can't be written leaves the setting unapplied.
    /// True if the entry changed.</summary>
    public static bool Apply(bool byUser, bool release, bool on, string? program, bool installed, string file, Func<string, bool> exists)
    {
        if (!release || program == null) return false;
        try { return byUser ? Set(on, program, file) : Reconcile(on, program, installed, file, exists); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>The Background portal (org.freedesktop.portal.Background), which writes a Flatpak's autostart entry outside
    /// its sandbox.</summary>
    public interface IBackgroundPortal
    {
        /// <summary>RequestBackground with <paramref name="autostart"/> and <paramref name="commandline"/> (run in the
        /// sandbox): whether the app now starts at sign-in, as the portal answered; null when there is no portal or no
        /// answer came.</summary>
        Task<bool?> RequestAsync(bool autostart, IReadOnlyList<string> commandline, string reason);
    }

    public const string PortalReason = "Start SCSKiller when you sign in";

    /// <summary>A Flatpak's apply, which can't read the entry the portal writes: only the user's toggle asks for what it says,
    /// the start asks nothing (the portal keeps the entry, and a desktop's background-apps setting may have removed it).
    /// Returns what to keep as <see cref="Settings.StartAtSignInGranted"/>: the portal's answer; with none, off after asking
    /// for on, else <paramref name="granted"/>.</summary>
    public static async Task<bool> ApplyFlatpak(bool byUser, bool release, bool on, string program, IBackgroundPortal portal, bool granted)
    {
        if (!release || !byUser || !program.StartsWith("/app/", StringComparison.Ordinal)) return granted;
        return await portal.RequestAsync(on, [program, WindowsStartup.TrayArg], PortalReason) ?? (on ? false : granted);
    }

    /// <summary>A Flatpak's checkbox: what the portal granted, never the setting the user asked for, which a denial leaves on.
    /// Off by default, as the default asked the portal nothing.</summary>
    public static bool FlatpakShows(Settings s) => s.StartAtSignInGranted;
}
