using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.App;

public sealed partial class ScsKiller
{
    /// <summary>A recursive watch of a recorder game's install while the app runs: every name made or renamed under it, an
    /// attribute changed (a folder made a junction keeps its name), a lost event (the buffer overflowed) and an error count
    /// as a change. Deletions can't add a marker. Only on a folder with a file id: a path alone can't tell a replaced install.</summary>
    sealed class InstallWatch : IDisposable
    {
        readonly FileSystemWatcher _watcher;
        readonly string _dir, _id;
        readonly Action? _idRead;
        long _events;
        volatile bool _broken;

        public InstallWatch(string dir, Action? idRead = null)
        {
            (_dir, _id, _idRead) = (dir, IdOf(dir) ?? throw new IOException("its folder has no file id"), idRead);
            _watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = true, InternalBufferSize = 64 << 10,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes };
            FileSystemEventHandler changed = (_, _) => Interlocked.Increment(ref _events);
            _watcher.Created += changed;
            _watcher.Changed += changed;
            _watcher.Renamed += (_, _) => Interlocked.Increment(ref _events);
            _watcher.Error += (_, _) => { _broken = true; Interlocked.Increment(ref _events); };
            _watcher.EnableRaisingEvents = true;
        }

        static string? IdOf(string dir) => GameFiles.Identity(dir) is ({ } id, _) ? Convert.ToHexString(id) : null;

        public long Events => Interlocked.Read(ref _events);

        /// <summary>No error, and the folder at its path is still the one it watches (a moved or replaced install keeps the old handle).</summary>
        public bool Sound()
        {
            if (_broken) return false;
            try
            {
                var same = IdOf(_dir) == _id;
                _idRead?.Invoke();
                return same;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }

        public void Dispose() => _watcher.Dispose();
    }

    readonly ConcurrentDictionary<string, InstallWatch> _watches = new(StringComparer.OrdinalIgnoreCase);   // install folder -> its watch
    // game id -> its install's watch, the watch's count when its last clean full walk began, and the lists it was checked against
    readonly ConcurrentDictionary<string, (InstallWatch Watch, long At, string Lists)> _watchedClean = new();
    [ThreadStatic] static bool t_linked;   // the full walk this thread just ran followed a link: what it leads to isn't watched
    readonly ConcurrentDictionary<string, bool> _unwatchedLogged = new();

    /// <summary>Tests: no install is watched, every pass walks in full as without a watch.</summary>
    internal bool WatchInstalls { get; set; } = OperatingSystem.IsWindows() && !Vendors.Wine.Running;

    static string ListsStamp => GameFiles.MarkersStamp + "|" + GameVerdicts.Current.AntiCheatStamp;

    /// <summary>The watch of the game's install, made if missing; null where none can be trusted: off Windows, a network or
    /// unknown drive, an exe outside the install (a second root), a watch that couldn't start or broke (then the pass walks in full).</summary>
    InstallWatch? WatchOf(Game g)
    {
        if (!WatchInstalls) return null;
        var dir = GameFiles.DirKey(g.InstallDir);
        if (!GameFiles.Inside(dir, g.ExePath))
        {
            if (_unwatchedLogged.TryAdd(g.Id, true)) RecorderLog($"{g.Name}: its install isn't watched (its exe is outside it): walked in full at each anti-cheat pass");
            return null;
        }
        if (_watches.TryGetValue(dir, out var w))
        {
            if (w.Sound()) return w;
            if (_watches.TryRemove(new(dir, w))) w.Dispose();
        }
        string? why = null;
        try
        {
            if (dir.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(dir).DriveType is not (DriveType.Fixed or DriveType.Removable)) why = "its install isn't on a local drive";
            else
            {
                var made = new InstallWatch(dir, WatchIdRead);
                if (_watches.TryAdd(dir, made)) return made;
                made.Dispose();
                return _watches.GetValueOrDefault(dir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException) { why = e.Message; }
        if (_unwatchedLogged.TryAdd(g.Id, true)) RecorderLog($"{g.Name}: its install isn't watched ({why}): walked in full at each anti-cheat pass");
        return null;
    }

    /// <summary>The game's last clean full walk began after its install's watch started, and nothing changed under it since,
    /// nor the lists it was checked against: only what lies outside the install needs a look (<see cref="QuickCheck"/>).</summary>
    bool WatchedClean(Game g) =>
        _watchedClean.TryGetValue(g.Id, out var c) && c.Lists == ListsStamp && GameFiles.Inside(g.InstallDir, g.ExePath)
        && _watches.TryGetValue(GameFiles.DirKey(g.InstallDir), out var w) && ReferenceEquals(w, c.Watch) && w.Sound()
        && c.Watch.Events == c.At;   // last: a change while the folder's id was read counts

    /// <summary>The anti-cheat check of a watched install nothing changed in: every check but the deep walk (the game's ids and
    /// exe names, the lists, Steam's launch options, TruckersMP, Riot, the install root's and exe folder's own entries, a
    /// hand-added game's folders above). Null when it didn't finish or couldn't read a folder.</summary>
    static AntiCheat? QuickCheck(Game g)
    {
        var late = false;
        var found = GameFiles.DetectAntiCheat(g, quick: true, late: () => late = true);
        return found is AntiCheat.EasyAntiCheat or AntiCheat.BattlEye || !late ? found : null;
    }

    /// <summary>Stops the watches of installs no recorder game has any more (all with <paramref name="keep"/> null).</summary>
    internal void DropWatches(IEnumerable<Game>? keep = null)
    {
        var kept = (keep ?? []).Select(g => GameFiles.DirKey(g.InstallDir)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, w) in _watches)
            if (!kept.Contains(dir) && _watches.TryRemove(new(dir, w))) w.Dispose();
    }

    // volume root -> its disk and whether that disk seeks (a rotating one); "?" when Windows can't tell, one for all such volumes
    static readonly ConcurrentDictionary<string, (string Disk, bool Rotating)> Disks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The volume root the install is on where it really is (a junction to another drive counts there).</summary>
    static string RootOf(string install)
    {
        try { return Path.GetPathRoot(GameFiles.FinalPath(install) ?? install) ?? ""; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>The disks the installs are on, each read on its own within a few seconds: one that doesn't answer by then is
    /// on the unknown disk this pass (a volume's answer is kept for the next).</summary>
    static async Task<Dictionary<string, (string Disk, bool Rotating)>> DisksOf(IEnumerable<string> installs)
    {
        var reads = installs.Distinct(StringComparer.OrdinalIgnoreCase).Select(i => (Install: i, Read: Task.Run(() => DiskOf(RootOf(i))))).ToList();
        await Task.WhenAny(Task.WhenAll(reads.Select(r => r.Read)), Task.Delay(TimeSpan.FromSeconds(5)));
        return reads.ToDictionary(r => r.Install, r => r.Read.IsCompletedSuccessfully ? r.Read.Result : ("?", false), StringComparer.OrdinalIgnoreCase);
    }

    readonly ConcurrentDictionary<string, Task> _diskWalking = new();   // disk -> the pass task walking it: a stalled one holds its disk past its pass

    /// <summary>The physical disk of the volume at <paramref name="root"/>, for one walk at a time per disk; a network share is its own.</summary>
    static (string Disk, bool Rotating) DiskOf(string root)
    {
        if (!OperatingSystem.IsWindows() || root.Length == 0) return ("?", false);
        try { if (root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network) return ("net:" + root, false); }
        catch (Exception e) when (e is ArgumentException or IOException) { return ("?", false); }
        return Disks.GetOrAdd(root, r =>
        {
            using var volume = OpenVolume(@"\\.\" + r.TrimEnd('\\'), 0, 3 /* read, write */, 0, 3 /* existing */, 0, 0);
            if (volume.IsInvalid) return ("?", false);
            var extents = new byte[64];   // VOLUME_DISK_EXTENTS: count, then DISK_EXTENT { DiskNumber, ... } at 8
            var disk = DeviceIoControl(volume, 0x560000 /* IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS */, null, 0, extents, extents.Length, out _, 0)
                && BitConverter.ToInt32(extents, 0) == 1 ? "disk:" + BitConverter.ToInt32(extents, 8) : "?";   // a volume over several disks: unknown
            var query = new byte[12];   // STORAGE_PROPERTY_QUERY { StorageDeviceSeekPenaltyProperty, PropertyStandardQuery }
            BitConverter.TryWriteBytes(query, 7);
            var seek = new byte[12];   // DEVICE_SEEK_PENALTY_DESCRIPTOR: IncursSeekPenalty at 8
            var rotating = DeviceIoControl(volume, 0x2D1400 /* IOCTL_STORAGE_QUERY_PROPERTY */, query, query.Length, seek, seek.Length, out _, 0) && seek[8] != 0;
            return (disk, rotating);
        });
    }

    /// <summary>Tests: called right after a watch reads its folder's id.</summary>
    internal Action? WatchIdRead { get; set; }

    /// <summary>Tests: the events the game's install watch has counted; -1 without one.</summary>
    internal long WatchCount(Game g) => _watches.TryGetValue(GameFiles.DirKey(g.InstallDir), out var w) ? w.Events : -1;

    /// <summary>Tests: the next pass checks the game by its watch alone (<see cref="WatchedClean"/>).</summary>
    internal bool WatchedNow(string gameId) => Games.FirstOrDefault(s => s.Game.Id == gameId)?.Game is { } g && WatchedClean(ArmTarget(g));

    readonly ConcurrentDictionary<string, DateTime> _passWalked = new();   // game id -> when a pass last checked it: one a pass left unchecked goes first in the next

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle OpenVolume(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll")]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, nint overlapped);
}
