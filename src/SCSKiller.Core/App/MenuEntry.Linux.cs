using System.Text;

namespace SCSKiller.Core.App;

/// <summary>An AppImage's entry in the desktop's application menu, which its package can't add: the package's own entry
/// (share/applications beside the app, its Exec left for the package to fill in) with Exec and TryExec naming the
/// AppImage's file, in $XDG_DATA_HOME/applications, and the package's icons in the hicolor theme there. Each start from an
/// AppImage writes it again, so it starts the copy that ran last, and TryExec hides it once that file is gone. A start
/// whose file is refused removes the entry naming that file. An entry of the same name that SCSKiller didn't write is
/// never changed, and the Hidden and NoDisplay a menu editor sets are kept.</summary>
public static class MenuEntry
{
    public const string AppId = "com.BlueHeisenberg.SCSKiller", FileName = AppId + ".desktop";
    // the key that makes an entry ours: a menu editor keeps the keys it doesn't know
    const string OwnKey = "X-SCSKiller-AppImage", Placeholder = "Exec=@EXEC@";
    static readonly string[] UsersKeys = ["Hidden", "NoDisplay"];

    /// <summary>$XDG_DATA_HOME, else ~/.local/share: a relative one is ignored, as the XDG spec says.</summary>
    public static string DataHome(Func<string, string?> env) =>
        env("XDG_DATA_HOME") is { Length: > 0 } d && Path.IsPathFullyQualified(d) ? d
            : Path.Combine(env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    public static string EntryFile(string dataHome) => Path.Combine(dataHome, "applications", FileName);

    /// <summary>This process's refresh: from an AppImage only, the package being the app's folder. What to log, or null.</summary>
    public static string? RefreshThis() =>
        Refresh(Environment.ProcessPath, Environment.GetEnvironmentVariable, Libc.RealPath, AppContext.BaseDirectory, Libc.UserId, Libc.Lstat, Libc.AccessAcl);

    /// <summary>The start's refresh when <paramref name="processPath"/> runs from an AppImage (<see cref="AppImage.Image"/>):
    /// the package's icons, then the entry, each written only when it differs. Null when it isn't an AppImage or nothing
    /// changed, else the line to log: what was written or removed, or why nothing was.</summary>
    internal static string? Refresh(string? processPath, Func<string, string?> env, Func<string, string?> realPath, string packageDir,
        uint uid, Func<string, (uint Uid, uint Mode)?> lstat, Func<string, int> acl)
    {
        if (AppImage.Image(processPath, env) is not { } image) return null;
        if (realPath(image) is not { } program) return $"Menu entry: {image} can't be found";
        if (Refused(program, env, uid, lstat, acl) is { } why) return Unlist(EntryFile(DataHome(env)), program, realPath, why);
        var dataHome = DataHome(env);
        var file = EntryFile(dataHome);
        try
        {
            var template = Path.Combine(packageDir, "share", "applications", FileName);
            if (!File.Exists(template)) return $"Menu entry: the package has no {template}";
            var now = Read(file);
            if (now is { Ours: false }) return $"Menu entry: {file} isn't SCSKiller's, left as it is";
            if (Fill(File.ReadAllText(template), program, now?.Keys) is not { } text) return $"Menu entry: {template} needs exactly one {Placeholder} line";
            var icons = Icons(packageDir, dataHome);
            if (!Write(file, now?.Text, text) && !icons) return null;
            return $"Menu entry: {file} starts {program}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Menu entry: {e.Message}"; }
    }

    /// <summary>The package's entry with its Exec placeholder, the one line <see cref="Placeholder"/> of its [Desktop Entry]
    /// group, naming <paramref name="program"/>, and the <see cref="UsersKeys"/> of the entry it replaces
    /// (<paramref name="was"/>); null when the template has no such line, or more than one.</summary>
    static string? Fill(string template, string program, IReadOnlyDictionary<string, string>? was = null)
    {
        var lines = template.Split('\n');
        var group = "";
        var at = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[')) group = line;
            else if (group == "[Desktop Entry]" && line == Placeholder)
            {
                if (at >= 0) return null;
                at = i;
            }
        }
        if (at < 0) return null;
        var kept = UsersKeys.Where(k => was?.ContainsKey(k) == true).Select(k => $"\n{k}={was![k]}");
        lines[at] = $"Exec={ExecArg(program)}\nTryExec={program}\n{OwnKey}=true{string.Concat(kept)}";
        return string.Join('\n', lines);
    }

    /// <summary>One quoted Exec argument: the quoting rule escapes " ` $ \ with a backslash, the string type then doubles each
    /// backslash, and a literal % is %%.</summary>
    internal static string ExecArg(string arg)
    {
        var q = new StringBuilder("\"");
        foreach (var c in arg)
            _ = c switch
            {
                '\\' => q.Append(@"\\\\"),
                '"' or '`' or '$' => q.Append(@"\\").Append(c),
                '%' => q.Append("%%"),
                _ => q.Append(c),
            };
        return q.Append('"').ToString();
    }

    /// <summary>Why <paramref name="program"/> can't be what the menu starts; null when it can. Never a path with a control
    /// character (a line of its own in the entry) or whitespace at either end, a quote, a backslash or a % (GLib looks for
    /// the file before reading %%), never in an AppImage's mount or a folder emptied at sign-out, and never where another
    /// user could replace it: the file and each folder above it belong to root or <paramref name="uid"/>, none is a link,
    /// none has group or other write (no group can be proven to hold this user alone), and none has an access ACL, whose
    /// named users the group bits don't show. <paramref name="lstat"/>: the path's own owner and mode. <paramref name="acl"/>: <see cref="Libc.AccessAcl"/>.</summary>
    internal static string? Refused(string program, Func<string, string?> env, uint uid, Func<string, (uint Uid, uint Mode)?> lstat, Func<string, int> acl)
    {
        if (program.Any(char.IsControl)) return "its path has a control character";
        if (program != program.Trim()) return "its path starts or ends with whitespace";   // Read trims each line
        if (program.IndexOfAny(['"', '\'', '\\', '%']) >= 0) return $"{program} has a quote, a backslash or a %";
        if (!Path.IsPathFullyQualified(program) || Path.GetFullPath(program) != program) return $"{program} isn't a full path";
        if (env("APPDIR") is { Length: > 0 } mount && AppImage.Under(program, mount) || program.Split('/').Any(p => p.StartsWith(".mount_", StringComparison.Ordinal)))
            return $"{program} is inside an AppImage's mount";
        if (env("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && AppImage.Under(program, runtime)) return $"{program} is in a folder emptied at sign-out";
        if (lstat(program) is { Mode: var mode } && (mode & 0xF000) is not (0x8000 or 0xA000)) return $"{program} isn't a file";
        return OwnPath.Exposed(program, uid, lstat, acl);   // a sticky /tmp refused too
    }

    /// <summary>The line for a <paramref name="program"/> refused for <paramref name="why"/>, after deleting the entry
    /// SCSKiller wrote for it: a regular file, never a link, with its key and a TryExec that is that path or resolves to it
    /// (<paramref name="realPath"/>), as one replaced by a link to the refused file does. Its icons stay, as other copies
    /// of the AppImage share them.</summary>
    static string Unlist(string file, string program, Func<string, string?> realPath, string why)
    {
        try
        {
            if (Libc.Lstat(file) is not { } node || (node.Mode & 0xF000) != 0x8000) return $"Menu entry: {why}";   // S_IFREG
            if (Read(file) is not { Ours: true } now || now.Keys.GetValueOrDefault("TryExec") is not { } named
                || named != program && realPath(named) != program) return $"Menu entry: {why}";
            File.Delete(file);
            return $"Menu entry: {file} removed: {why}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Menu entry: {why}, {file} not removed: {e.Message}"; }
    }

    /// <summary>An entry file as read: its [Desktop Entry] keys, and whether SCSKiller wrote it.</summary>
    sealed record Found(string Text, IReadOnlyDictionary<string, string> Keys)
    {
        public bool Ours => Keys.GetValueOrDefault(OwnKey) == "true";
    }

    /// <summary>Null when there is no file. One that can't be read throws, and a link to nothing is another's: neither is
    /// ever written over.</summary>
    static Found? Read(string file)
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

    /// <summary>Each size of the package's icon (share/icons/hicolor/&lt;size&gt;/apps) into the user's hicolor theme, which
    /// a desktop reads without a cache. True if one changed.</summary>
    static bool Icons(string packageDir, string dataHome)
    {
        var theme = Path.Combine(packageDir, "share", "icons", "hicolor");
        var changed = false;
        foreach (var size in Directory.Exists(theme) ? Directory.GetDirectories(theme) : [])
        {
            var icon = Path.Combine(size, "apps", AppId + ".png");
            if (!File.Exists(icon)) continue;
            var to = Path.Combine(dataHome, "icons", "hicolor", Path.GetFileName(size), "apps", AppId + ".png");
            var bytes = File.ReadAllBytes(icon);
            changed |= Write(to, File.Exists(to) ? File.ReadAllBytes(to) : null, bytes);
        }
        return changed;
    }

    internal static bool Write(string file, string? now, string text) => Write(file, now == null ? null : Encoding.UTF8.GetBytes(now), Encoding.UTF8.GetBytes(text));

    // a fresh temp, never a link planted at its name, renamed over the file, never written through a link there
    static bool Write(string file, byte[]? now, byte[] bytes)
    {
        if (now != null && now.AsSpan().SequenceEqual(bytes)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var (stream, temp) = ScsKiller.CreateTemp(file, ".tmp");
        try
        {
            using (stream) stream.Write(bytes);
            File.Move(temp, file, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        return true;
    }
}
