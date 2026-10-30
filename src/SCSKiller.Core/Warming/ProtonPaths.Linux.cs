using SCSKiller.Core.App;

namespace SCSKiller.Core.Warming;

/// <summary>Paths between Core's view and a warm's in a Wine prefix, through the prefix's dosdevices drives as they are when
/// it's made. A Linux path takes the deepest drive whose root is one of its folders (by device and inode, the lowest
/// letter on a tie), as Wine names it. Proton makes z: (the root) before it runs anything, so a prefix without one gets it.</summary>
sealed class ProtonPaths
{
    readonly string _devices;
    readonly List<(char Letter, string Root, uint Major, uint Minor, ulong Inode)> _drives = [];

    /// <param name="prefix">the prefix folder (compatdata's pfx)</param>
    public ProtonPaths(string prefix)
    {
        _devices = Path.Combine(prefix, "dosdevices");
        for (var c = 'a'; c <= 'z'; c++)
            if (Root(c) is { } root && Libc.Stat(root) is { } s) _drives.Add((c, root, s.DevMajor, s.DevMinor, s.Inode));
        if (!_drives.Any(d => d.Letter == 'z') && Libc.Stat("/") is { } r) _drives.Add(('z', "/", r.DevMajor, r.DevMinor, r.Inode));
    }

    /// <summary>The folder drive <paramref name="letter"/> names, its links resolved; null when it names none.</summary>
    string? Root(char letter)
    {
        var link = Path.Combine(_devices, letter + ":");
        return Directory.Exists(link) ? Libc.RealPath(link) : null;
    }

    /// <summary><paramref name="host"/> (a Linux path, which need not exist) as the warm opens it: "X:\..." for the deepest
    /// drive above it, else \\?\unix\... (Wine's form for a path no drive holds).</summary>
    public string WarmPath(string host)
    {
        var full = Path.GetFullPath(host);
        for (var dir = full; dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (Libc.Stat(dir) is not { } s) continue;
            foreach (var d in _drives)
                if ((d.Major, d.Minor, d.Inode) == (s.DevMajor, s.DevMinor, s.Inode))
                    return $"{char.ToUpperInvariant(d.Letter)}:\\{full[dir.Length..].TrimStart('/').Replace('/', '\\')}";
        }
        return @"\\?\unix" + full.Replace('/', '\\');
    }

    /// <summary><paramref name="warm"/> (a Windows path the warm names, "X:\..." or with \\?\ before it) as Core opens it;
    /// a path on no drive, or not of that form, comes back as it is.</summary>
    public string HostPath(string warm)
    {
        var p = warm.StartsWith(@"\\?\", StringComparison.Ordinal) || warm.StartsWith(@"\??\", StringComparison.Ordinal) ? warm[4..] : warm;
        if (p.StartsWith(@"unix\", StringComparison.OrdinalIgnoreCase)) return p[4..].Replace('\\', '/');
        if (p.Length < 2 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || (p.Length > 2 && p[2] != '\\')) return warm;
        var letter = char.ToLowerInvariant(p[0]);
        // a drive Proton made since, such as s: for the game's library
        if ((_drives.FirstOrDefault(d => d.Letter == letter).Root ?? Root(letter)) is not { } root) return warm;
        var rest = p.Length > 3 ? p[3..].Replace('\\', '/') : "";
        return rest.Length == 0 ? root : Path.Join(root, rest);
    }
}
