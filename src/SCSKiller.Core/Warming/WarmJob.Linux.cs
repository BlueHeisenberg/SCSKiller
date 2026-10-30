using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Warming;

/// <summary>The warms Core runs under Wine, where no job object holds them: the Wine processes whose environment carries
/// <see cref="Variable"/> with the job's <see cref="Token"/>, limited to the warm parent (an image named scskiller_warm.exe)
/// and its staged child (an image in a stage-* folder of the run's work folder, or of a Flatpak Steam game's exchange's,
/// <see cref="FlatpakExchange"/>, and then in its sandbox alone, <see cref="Within"/>); and the native Vulkan warm's processes
/// that carry it, scskiller_warm and its fossilize-replay processes, by their images' names. The launcher chain, the
/// prefix's wineserver and Wine's own services inherit the variable too and are never members. Told from /proc alone and signalled
/// through a pidfd only: a member none can be had for is left alone. Nothing ends the members when Core ends: the warm's
/// alive file stops it, and <see cref="EndOrphans"/> ends what is left at the next warm's start. Folders and names are
/// told apart case and all, as Linux's file systems tell them.</summary>
public sealed class WarmJob
{
    public const string Variable = "SCSKILLER_WARM_JOB";
    const string WarmExe = "scskiller_warm.exe", NativeWarm = "scskiller_warm", NativeReplayer = "fossilize-replay";

    // a reused pid has another start time: a token tells whether the Core that made it still runs
    static readonly string CoreId = $"{Environment.ProcessId}.{ProcessTree.Created(Environment.ProcessId) ?? 0}";

    /// <summary>This Core's: the warm parents of every run's job, and the native warms' replayers. It tells membership alone,
    /// never signals, and holds no staged child: which stage folders are this Core's depends on its data folder (<see cref="In"/>).</summary>
    public static WarmJob Current { get; } = new(CoreId, null, null, null);

    readonly string? _workDir;
    readonly string[]? _games, _exchanges;
    readonly List<string> _staged = [];   // the run's exchanges' work folders; under itself

    /// <summary>The calls a signal goes through. Replaceable for tests.</summary>
    internal ISignals Signals { get; init; } = LibcSignals.Instance;

    /// <summary>Told once when a member is left alone for want of a pidfd.</summary>
    internal Action<string>? Log { get; init; }

    /// <summary>Whether a process may be a member at all, told before what it can set itself (its environment, name and
    /// image): a Flatpak Steam game's warm's are its sandbox's alone (<see cref="FlatpakSandbox.Holds"/>).</summary>
    internal Func<int, bool>? Within { get; set; }

    int _told;

    /// <summary><see cref="Variable"/>'s value: the Core's pid and start time (clock ticks since boot), then the run's guid.
    /// <see cref="Current"/>'s is the Core's part alone.</summary>
    public string Token { get; }

    /// <summary>A run's job, whose staged children run from <paramref name="workDir"/>'s stage folders.</summary>
    public WarmJob(string workDir) : this($"{CoreId}.{Guid.NewGuid():N}", Path.GetFullPath(workDir), null, null) { }

    WarmJob(string token, string? workDir, string[]? games, string[]? exchanges) => (Token, _workDir, _games, _exchanges) = (token, workDir, games, exchanges);

    /// <summary>Adds an exchange's work folder, whose stage folders the run's staged children run from too.</summary>
    internal void Stage(string workDir)
    {
        lock (_staged) _staged.Add(Path.GetFullPath(workDir));
    }

    /// <summary><see cref="Current"/> with the staged children of every run in <paramref name="dataDir"/>: images in a stage-*
    /// folder of a work folder of its games (games/&lt;id&gt;/work/stage-*) or of an exchange's (flatpak/&lt;name&gt;/work/stage-*),
    /// as the kernel names them, symlinks resolved. Membership alone, as <see cref="Current"/>.</summary>
    public WarmJob In(string dataDir)
    {
        if (_workDir != null) throw new InvalidOperationException("a run's job is a run's alone");
        return new(Token, null, Names(Path.Combine(dataDir, "games")), Names(Path.Combine(dataDir, FlatpakExchange.Folder)));
    }

    /// <summary>The folder's full path, and the kernel's name for it when a symlink on the way makes that another.</summary>
    static string[] Names(string dir)
    {
        var full = Path.GetFullPath(dir);
        return GameFiles.FinalPath(full) is { } real && real != full ? [full, real] : [full];
    }

    public bool Contains(int pid) => Image(pid) != null;

    /// <summary>The members, warm parents first.</summary>
    public List<int> Members()
    {
        List<int> parents = [], children = [];
        foreach (var pid in ProcessTree.Pids())
            if (Image(pid) is { } image) (IsWarm(image) ? parents : children).Add(pid);
        return [.. parents, .. children];
    }

    /// <summary>SIGKILL to the members; false when there was none.</summary>
    public bool Kill() => Signal(Libc.SigKill);

    /// <summary>SIGSTOP or SIGCONT to the members: a stopped warm's heartbeat stops with it, as a suspended one's does on
    /// Windows. False when there was none.</summary>
    public bool Suspend(bool suspend) => Signal(suspend ? Libc.SigStop : Libc.SigCont);

    bool Signal(int signal)
    {
        if (_workDir == null) throw new InvalidOperationException("only a run's job is signalled");
        var sent = new HashSet<int>();
        var any = false;
        // again while a pass finds one it hadn't: a parent may start its child meanwhile
        for (var pass = 0; pass < 5; pass++)
        {
            var more = false;
            foreach (var pid in Members())
                if (sent.Add(pid))
                {
                    any |= Send(pid, signal);
                    more = true;
                }
            if (!more) break;
        }
        return any;
    }

    /// <summary>Signals a member through a pidfd opened before its membership is checked again: a pid freed and reused after
    /// the check names another process, which the pidfd doesn't reach. Exited meanwhile, or no pidfd to be had: nothing is
    /// sent, and the member counts as not ours.</summary>
    bool Send(int pid, int signal)
    {
        using var fd = Signals.PidFd(pid, out var error);
        if (fd == null)
        {
            if (error != Libc.NoSuchProcess && Contains(pid) && Interlocked.Exchange(ref _told, 1) == 0)
                Log?.Invoke($"a warm process (pid {pid}) is left alone: no pidfd could be opened for it ({Marshal.GetPInvokeErrorMessage(error)})");
            return false;
        }
        if (!Contains(pid)) return false;
        Signals.Signal(fd, signal);
        return true;
    }

    /// <summary>A member's image; null for any other process.</summary>
    string? Image(int pid)
    {
        if (Within is { } within && !within(pid)) return null;
        if (ProcessTree.IsWine(pid))
            return Carries(pid) && ProcessTree.ImagePath(pid) is { } image && (IsWarm(image) || Staged(image)) ? image : null;
        return ProcessTree.ImagePath(pid) is { } native && IsNative(native) && Carries(pid) ? native : null;
    }

    bool Carries(int pid) => ProcessTree.StartEnvironment(pid, Variable) is { } token
        && (_workDir == null ? token.StartsWith(Token + ".", StringComparison.Ordinal) : token == Token);

    static bool IsWarm(string image) => Path.GetFileName(image) is var name && (name.Equals(WarmExe, GameFiles.Names) || name == NativeWarm);

    /// <summary>A native Vulkan warm's process: no Wine process is.</summary>
    static bool IsNative(string image) => Path.GetFileName(image) is NativeWarm or NativeReplayer;

    /// <summary>The image is in a stage-* folder of the run's work folder, or of a work folder of the data folder's games for
    /// <see cref="In"/>'s view. The kernel names it with the folders' symlinks resolved.</summary>
    bool Staged(string image)
    {
        if (_workDir == null) return _games != null && _games.Any(games => InGamesStage(games, image)) || _exchanges != null && _exchanges.Any(x => InExchangeStage(x, image));
        string[] staged;
        lock (_staged) staged = [.. _staged];
        foreach (var work in staged.Prepend(_workDir).SelectMany(w => new[] { w, GameFiles.FinalPath(w) }))
            if (work != null && GameFiles.Inside(work, image, GameFiles.Names) && Path.GetRelativePath(work, image).Split(Path.DirectorySeparatorChar) is [var stage, _, ..] && IsStage(stage))
                return true;
        return false;
    }

    static bool IsStage(string name) => name.StartsWith("stage-", GameFiles.Names);

    /// <summary>&lt;name&gt;/work/stage-*/... under <paramref name="exchanges"/> (<see cref="FlatpakExchange.Folder"/>).</summary>
    static bool InExchangeStage(string exchanges, string image) => GameFiles.Inside(exchanges, image, GameFiles.Names)
        && Path.GetRelativePath(exchanges, image).Split(Path.DirectorySeparatorChar) is [_, FlatpakExchange.Work, var stage, _, ..] && IsStage(stage);

    /// <summary>&lt;name&gt;/bin/... under <paramref name="exchanges"/>, where a Flatpak Steam game's warm parent runs from.</summary>
    static bool InExchangeBin(string exchanges, string image) => GameFiles.Inside(exchanges, image, GameFiles.Names)
        && Path.GetRelativePath(exchanges, image).Split(Path.DirectorySeparatorChar) is [_, FlatpakExchange.Bin, _, ..];

    /// <summary>games/&lt;id&gt;/&lt;work folder&gt;/stage-*/... under <paramref name="games"/>.</summary>
    static bool InGamesStage(string games, string image) => GameFiles.Inside(games, image, GameFiles.Names)
        && Path.GetRelativePath(games, image).Split(Path.DirectorySeparatorChar) is [_, var work, var stage, _, ..] && ScsKiller.IsWorkDir(work) && IsStage(stage);

    /// <summary>Ends, with SIGKILL, the warm parents and staged children a Core that is gone left (its pid no longer runs with
    /// the start time in their token): those of <paramref name="dataDir"/> alone, a parent's image in its native folder
    /// (where the host runs the warm from, a native warm's replayer too) or an exchange's bin folder, a child's in a stage
    /// folder of its games or of an exchange (<see cref="FlatpakExchange"/>). A live
    /// Core's run, the app's or a CLI's, is never touched, nor any process but these. Through a pidfd opened before the
    /// process is checked again: one none can be had for is left alone and told. Each process ended is told.</summary>
    internal static List<int> EndOrphans(string dataDir, Action<string>? log, ISignals? signals = null)
    {
        signals ??= LibcSignals.Instance;
        var (natives, games, exchanges) = (Names(Path.Combine(dataDir, "native")), Names(Path.Combine(dataDir, "games")), Names(Path.Combine(dataDir, FlatpakExchange.Folder)));
        string? Orphan(int pid)
        {
            var wine = ProcessTree.IsWine(pid);
            if (!wine && (ProcessTree.ImagePath(pid) is not { } exe || !IsNative(exe))) return null;
            return ProcessTree.StartEnvironment(pid, Variable) is { } token && CoreGone(token) && ProcessTree.ImagePath(pid) is { } image
                && ((wine ? IsWarm(image) : IsNative(image)) && natives.Any(dir => GameFiles.Inside(dir, image, GameFiles.Names))
                    || wine && games.Any(dir => InGamesStage(dir, image))
                    || wine && exchanges.Any(dir => IsWarm(image) && InExchangeBin(dir, image) || InExchangeStage(dir, image))) ? image : null;
        }
        var ended = new List<int>();
        foreach (var pid in ProcessTree.Pids())
        {
            if (Orphan(pid) is null) continue;
            using var fd = signals.PidFd(pid, out var error);
            if (fd == null)
            {
                if (error != Libc.NoSuchProcess)
                    log?.Invoke($"a compile process left running by an SCSKiller that has exited (pid {pid}) is left alone: no pidfd could be opened for it ({Marshal.GetPInvokeErrorMessage(error)})");
                continue;
            }
            if (Orphan(pid) is not { } image || !signals.Signal(fd, Libc.SigKill)) continue;
            ended.Add(pid);
            log?.Invoke($"ended {Path.GetFileName(image)} (pid {pid}), a compile process left running by an SCSKiller that has exited");
        }
        return ended;
    }

    /// <summary>A run's token (pid.start.guid) whose Core no longer runs: its pid is free, or another process's.</summary>
    static bool CoreGone(string token) =>
        token.Split('.') is [var pid, var start, var run] && run.Length == 32
        && int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var core) && core > 0
        && long.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out var started)
        && ProcessTree.Created(core) != started;
}

/// <summary>The kernel calls <see cref="WarmJob"/> signals through.</summary>
interface ISignals
{
    SafeFileHandle? PidFd(int pid, out int error);
    bool Signal(SafeFileHandle pidfd, int signal);
}

sealed class LibcSignals : ISignals
{
    public static readonly LibcSignals Instance = new();

    public SafeFileHandle? PidFd(int pid, out int error) => Libc.PidFd(pid, out error);

    public bool Signal(SafeFileHandle pidfd, int signal) => Libc.Signal(pidfd, signal);
}
