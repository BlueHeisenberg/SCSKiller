namespace SCSKiller.Core.App;

static partial class NamedMutex
{
    // .NET keeps Local\ per setsid session (a terminal's CLI and the app miss each other) and Global\ where every user writes
    static readonly NamedWaitHandleOptions UserWide = new() { CurrentUserOnly = true, CurrentSessionOnly = false };

    // where .NET up to 10.0.12 keeps the names, whatever TMPDIR says; tests set another
    internal static string Legacy = $"/tmp/.dotnet-uid{Libc.EffectiveUserId}";

    // the running .NET's, which picks the folder; tests set another
    internal static Version Dotnet = Environment.Version;

    // probe names opened, for tests
    internal static int Probes;

    static string? _root, _made;   // the folder found, and the one below which missing folders are made (HOME, as .NET does)

    internal static partial Mutex Open(string name) => Open(name, null);

    internal static partial bool Held(string name) => Held(name, null);

    /// <summary>The folder the running .NET keeps the names in (<see cref="Layout"/>), trusted before any name opens
    /// (<see cref="Untrusted"/>), then confirmed by where .NET keeps a probe name (its /proc/self/fd link). Null with why
    /// while it can't be, tried again at the next call.</summary>
    internal static string? Root(out string? why)
    {
        why = null;
        if (_root != null) return _root;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Layout(Dotnet, Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), home, Legacy, out var underHome, out why);
        if (root == null) return null;
        var made = underHome ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)) : null;
        // before any name opens: .NET up to 10.0.12 follows a link here and makes the folder it names 0700, from 10.0.13 it
        // takes a folder open to others as it is
        if ((why = Untrusted(root, made)) != null) return null;
        var probe = "SCSKiller.probe-" + Guid.NewGuid().ToString("N");
        var tail = "/shm/global/" + probe;
        Interlocked.Increment(ref Probes);
        try
        {
            using var m = new Mutex(false, probe, UserWide);
            foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
                if (Target(fd) is { } file && file.EndsWith(tail, StringComparison.Ordinal))
                {
                    var actual = file[..^tail.Length];
                    if (actual == Libc.RealPath(root))
                    {
                        _made = made;
                        return _root = root;
                    }
                    why = $"The locks are in {actual}, not in {root} where SCSKiller checks them";
                    return null;
                }
            why = "The folder where SCSKiller keeps its locks can't be found: /proc/self/fd doesn't show it";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = $"The folder where SCSKiller keeps its locks can't be found: {e.Message.TrimEnd(';', ' ').Replace("; ", ", ")}";   // a line users see
        }
        return null;
    }

    /// <summary>Where a .NET of version <paramref name="dotnet"/> keeps the names, as it picks the folder: up to 10.0.12
    /// <paramref name="legacy"/>, from 10.0.13 .dotnet in <paramref name="xdg"/> (XDG_RUNTIME_DIR), or under
    /// <paramref name="home"/> (<paramref name="underHome"/>) when that is unset or empty. Null with why when there is none,
    /// or it isn't <see cref="Plain"/>.</summary>
    internal static string? Layout(Version dotnet, string? xdg, string home, string legacy, out bool underHome, out string? why)
    {
        (underHome, why) = (false, null);
        if (dotnet < new Version(10, 0, 13)) return legacy;
        if (!string.IsNullOrEmpty(xdg))
        {
            if (Plain(xdg)) return Path.Combine(Path.GetFullPath(xdg), ".dotnet");
            why = $"XDG_RUNTIME_DIR ({xdg}) isn't a full path without . or .., so SCSKiller has nowhere to keep its locks";   // nor .NET when relative: no fallback
            return null;
        }
        if (!Plain(home))
        {
            why = home.Length == 0 ? "SCSKiller has no home folder to keep its locks in"
                : $"The home folder ({home}) isn't a full path without . or .., so SCSKiller has nowhere to keep its locks";
            return null;
        }
        underHome = true;
        return Path.Combine(Path.GetFullPath(home), ".dotnet", "corefx", "Runtime", "SharedMemory", ".dotnet");
    }

    // .NET takes the path as it is: the folders checked from / down are the ones the kernel walks only without . or .., as
    // a .. after a link leaves the link's target
    static bool Plain(string path) => Path.IsPathFullyQualified(path) && !path.Split('/').Any(s => s is "." or "..");

    static string? Target(string fd)
    {
        try { return new FileInfo(fd).LinkTarget; }
        catch (IOException) { return null; }   // closed meanwhile
    }

    internal static partial string? Unsafe(string? root)
    {
        if (root != null) return Untrusted(root, null);
        return Root(out var why) is { } found ? Untrusted(found, _made) : why;
    }

    /// <summary>Why another user could replace or empty <paramref name="root"/>: a folder above it they could
    /// (<see cref="Exposed"/>), or it or its shm folder not a private folder of the user's (<see cref="NotPrivate"/>). Null
    /// when nothing is. <paramref name="made"/>: as for <see cref="Exposed"/>.</summary>
    static string? Untrusted(string root, string? made) => (Path.GetDirectoryName(root) is { } parent ? Exposed(parent, made) : null) ?? NotPrivate(root);

    /// <summary>Why another user could replace a folder from / down to <paramref name="dir"/> (<see cref="OwnPath.Node"/>, a
    /// sticky folder of root's such as /tmp allowed), null when none could. A missing one below <paramref name="made"/> is
    /// made 0700 once every folder above it passed. <paramref name="lstat"/>, <paramref name="acl"/>: tests set others.</summary>
    internal static string? Exposed(string dir, string? made = null, Func<string, (uint Uid, uint Mode)?>? lstat = null, Func<string, int>? acl = null)
    {
        var chain = new Stack<string>();
        for (string? d = dir; d != null; d = Path.GetDirectoryName(d)) chain.Push(d);
        foreach (var d in chain)
        {
            if (made != null && d.StartsWith(made + "/", StringComparison.Ordinal)) Libc.MakePrivateDir(d);
            if (OwnPath.Node(d, Libc.EffectiveUserId, lstat ?? Libc.Lstat, acl ?? Libc.AccessAcl, stickyRoot: true) is { } why)
                return $"The folder where SCSKiller keeps its locks can't be trusted: {why}";
        }
        return null;
    }

    /// <summary>Why <paramref name="root"/> or its shm folder isn't a private folder of the user's, null when both are. A
    /// missing one is made 0700, shm only once root passed. Inside a private folder of ours nobody else can make, move or
    /// replace anything.</summary>
    static string? NotPrivate(string root)
    {
        foreach (var dir in new[] { root, Path.Combine(root, "shm") })
        {
            Libc.MakePrivateDir(dir);
            var why = Libc.Lstat(dir) switch
            {
                null => "can't be read",
                var (_, mode) when (mode & Libc.ModeType) != Libc.ModeDirectory => "is a link or no folder",
                var (owner, _) when owner != Libc.EffectiveUserId => "belongs to another user",
                var (_, mode) when (mode & Libc.ModePermissions) != Libc.ModeOwnerOnly => "is open to other users",
                _ when Libc.AccessAcl(dir) is var acl and not (Libc.NoData or Libc.NotSupported) => acl == 0 ? "has an ACL" : "has an ACL that can't be read",
                _ => null,
            };
            if (why != null) return $"{dir}, where SCSKiller keeps its locks, {why}";
        }
        return null;
    }

    // root: the folder checked, the one .NET uses but in tests (.NET opens the name there whatever it is)
    internal static Mutex Open(string name, string? root) => Unsafe(root) is { } why ? throw new IOException(why) : new(false, Bare(name), UserWide);

    internal static bool Held(string name, string? root)
    {
        if (Unsafe(root) != null) return true;
        try
        {
            if (!Mutex.TryOpenExisting(Bare(name), UserWide, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    // the options refuse a Local\ prefix
    internal static string Bare(string name) =>
        name.StartsWith(@"Local\", StringComparison.Ordinal) ? name[6..] : name.StartsWith(@"Global\", StringComparison.Ordinal) ? name[7..] : name;
}
