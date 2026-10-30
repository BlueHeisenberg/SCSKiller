using SCSKiller.Core.App;

namespace SCSKiller.Core.Warming;

static partial class ProcessTree
{
    public static partial void Suspend(int rootPid, bool suspend)
    {
        foreach (var pid in WithDescendants(rootPid)) Libc.Signal(pid, suspend ? Libc.SigStop : Libc.SigCont);   // exited meanwhile: no harm
    }

    public static partial List<(int Pid, int Parent, string Exe)> Snapshot()
    {
        var all = new List<(int, int, string)>();
        foreach (var pid in Pids())
            if (Stat(pid) is { } s && Lives(pid, s)) all.Add((pid, s.Parent, Exe(pid, s.Name)));
        return all;
    }

    /// <summary>The process hasn't exited and isn't exiting as a whole (<see cref="Dying"/>).</summary>
    internal static bool Lives(int pid) => Stat(pid) is { } s && Lives(pid, s);

    static bool Lives(int pid, (string Name, char State, int Parent, uint Flags, int Threads, long Started) s) => !Ended(s.State, s.Threads) && !Dying(pid, s.Flags);

    // a zombie runs nothing, though its stat name may still be the game's: a leader whose other threads run shows Z too
    static bool Ended(char state, int threads) => state == 'X' || state == 'Z' && threads <= 1;

    /// <summary>The process is exiting as a whole: its leader is past its exit (<paramref name="flags"/>), which leaves its
    /// path, command line and environment unreadable, and each thread is exiting or has the SIGKILL a group exit sends it.
    /// A leader that exited alone, its other threads running, isn't.</summary>
    static bool Dying(int pid, uint flags)
    {
        if ((flags & PfExiting) == 0) return false;
        try { return Directory.EnumerateDirectories($"/proc/{pid}/task").All(ThreadEnds); }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }

        static bool ThreadEnds(string task)
        {
            if (Stat(task) is not { } s || (s.Flags & PfExiting) != 0) return true;   // no stat: reaped meanwhile
            try
            {
                foreach (var line in File.ReadLines(Path.Combine(task, "status")))
                    if (line.StartsWith("SigPnd:", StringComparison.Ordinal))
                        return ulong.TryParse(line.AsSpan(7).Trim(), System.Globalization.NumberStyles.HexNumber, null, out var pending) && (pending & SigKillPending) != 0;
            }
            catch (IOException) { return true; }
            return false;
        }
    }

    const uint PfExiting = 0x4;
    const ulong SigKillPending = 1UL << 8;   // SigPnd's bit n-1 is signal n

    /// <summary>The process runs and started at <paramref name="started"/> (<see cref="Created"/>): not once it has exited,
    /// nor another process given its pid since.</summary>
    internal static bool Runs(int pid, long started) => Stat(pid) is { } s && s.Started == started && !Ended(s.State, s.Threads);

    internal static IEnumerable<int> Pids()
    {
        if (!OperatingSystem.IsLinux()) yield break;   // macOS has no /proc: no game is seen running
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(dir), out var pid)) yield return pid;
    }

    /// <summary>The process runs a Windows exe under Wine.</summary>
    internal static bool IsWine(int pid) => Link(pid) is { } image && IsWine(image);

    /// <summary><paramref name="name"/>'s value in the environment the process started with; null when it has none or it
    /// can't be read (another user's process).</summary>
    internal static string? StartEnvironment(int pid, string name) => StartEnvironment(pid)?.GetValueOrDefault(name);

    /// <summary>The environment the process started with, the first value of a name kept; null when it can't be read.</summary>
    internal static Dictionary<string, string>? StartEnvironment(int pid)
    {
        string environ;
        try { environ = File.ReadAllText($"/proc/{pid}/environ"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in environ.Split('\0'))
            if (entry.IndexOf('=') is var at and > 0) vars.TryAdd(entry[..at], entry[(at + 1)..]);
        return vars;
    }

    /// <summary>The paths of the files the process has open, as the kernel names them (links resolved); empty when they can't be read.</summary>
    internal static List<string> OpenPaths(int pid)
    {
        var paths = new List<string>();
        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
                try { if (new FileInfo(fd).LinkTarget is { } t && t.StartsWith('/') && !t.EndsWith(" (deleted)", StringComparison.Ordinal)) paths.Add(t); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return paths;
    }

    /// <summary>The process's arguments, argv[0] first; null when they can't be read (it exited, another user's process).</summary>
    internal static string[]? CommandLine(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/cmdline").TrimEnd('\0').Split('\0'); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static partial string? ImagePath(int pid)
    {
        if (Link(pid) is not { } image) return null;
        if (!IsWine(image)) return image;
        // a Wine process maps its exe from the Unix path Wine opened, whatever drive its command line names
        if (WindowsName(pid) is not { } name) return null;
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/maps"))
                if (line.IndexOf('/') is var at and >= 0 && Undeleted(line[at..]) is var file && Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase)) return file;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    // the start time in clock ticks since boot
    internal static partial long? Created(int pid) => Stat(pid)?.Started;

    /// <summary>The process's parent as this process's pid namespace numbers it; null when it has exited.</summary>
    internal static int? Parent(int pid) => Stat(pid)?.Parent;

    /// <summary>The process's pid namespace, by its inode; null when it has exited or can't be read.</summary>
    internal static ulong? PidNamespace(int pid)
    {
        try
        {
            return new FileInfo($"/proc/{pid}/ns/pid").LinkTarget is { } t && t.StartsWith("pid:[", StringComparison.Ordinal) && t.EndsWith(']')
                && ulong.TryParse(t[5..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ns) ? ns : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>/proc/pid/stat's name (at most 15 bytes), state, parent, kernel flags, thread count and start time; null when
    /// it has exited.</summary>
    static (string Name, char State, int Parent, uint Flags, int Threads, long Started)? Stat(int pid) => Stat($"/proc/{pid}");

    /// <summary><see cref="Stat(int)"/> of a /proc/pid or /proc/pid/task/tid folder.</summary>
    static (string Name, char State, int Parent, uint Flags, int Threads, long Started)? Stat(string dir)
    {
        string stat;
        try { stat = File.ReadAllText(Path.Combine(dir, "stat")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        // pid (name) state ppid ... flags (field 9) ... starttime (field 22): the name may hold spaces and parentheses
        int open = stat.IndexOf('('), close = stat.LastIndexOf(')');
        if (open < 0 || close < open) return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 19 && int.TryParse(fields[1], out var parent) && uint.TryParse(fields[6], out var flags) && int.TryParse(fields[17], out var threads)
            && long.TryParse(fields[19], out var started) ? (stat[(open + 1)..close], fields[0][0], parent, flags, threads, started) : null;
    }

    /// <summary>The image's file name: a Wine process's Windows exe, else its executable's; the stat name when the
    /// executable can't be read (another user's process, a kernel thread).</summary>
    static string Exe(int pid, string statName) =>
        Link(pid) is not { } image ? statName : !IsWine(image) ? Path.GetFileName(image) : WindowsName(pid) ?? statName;

    /// <summary>The process's executable; null when it can't be read.</summary>
    static string? Link(int pid)
    {
        try { return new FileInfo($"/proc/{pid}/exe").LinkTarget is { } t ? Undeleted(t) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // a staged warm's copy is deleted once Wine reports its process ended, while its exit still runs: /proc then marks the path
    static string Undeleted(string path) => path.EndsWith(" (deleted)", StringComparison.Ordinal) ? path[..^10] : path;

    /// <summary>Wine runs every Windows process as its loader.</summary>
    static bool IsWine(string image) => Path.GetFileName(image) is "wine" or "wine64" or "wine-preloader" or "wine64-preloader";

    /// <summary>The exe file name a Wine process's command line starts with (Wine sets its argv to the Windows command
    /// line), with the .exe a Windows launch adds when it's left out; null when it has none.</summary>
    static string? WindowsName(int pid)
    {
        string cmdline;
        try { cmdline = File.ReadAllText($"/proc/{pid}/cmdline"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var argv0 = cmdline.Split('\0')[0];
        var name = argv0[(argv0.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.Length == 0 ? null : name.Contains('.') ? name : name + ".exe";
    }
}
