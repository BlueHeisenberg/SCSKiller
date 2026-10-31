using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.App;

/// <summary><c>%LOCALAPPDATA%\D3DSCache</c>: one folder per exe path of SQLite dbs (<c>*.dxcache</c>, WAL) with an
/// <c>app_id(exe_path)</c> table. Opened immutable (no lock, no -shm/-wal write); rows only in the -wal are found by scanning it.</summary>
public static partial class D3DSCache
{
    /// <summary>Folders whose every exe path is the game's; skipped on doubt (an unreadable db, no path, another path).</summary>
    public static IReadOnlyList<string> FoldersOf(string root, Game g)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        foreach (var dir in Directory.EnumerateDirectories(root))
            if (IsGames(dir, g) == true) found.Add(dir);
        return found;
    }

    /// <summary>True: every exe path the folder names is the game's; false: a db names another exe (its WAL never read);
    /// null on doubt (an unreadable file, no path).</summary>
    public static bool? IsGames(string dir, Game g)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles();
            var dbs = files.Where(f => f.Name.EndsWith(".dxcache", StringComparison.OrdinalIgnoreCase)).ToList();
            var stamp = string.Join('|', dbs.Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));
            if (!DbPaths.TryGetValue(dir, out var known) || known.Stamp != stamp)
            {
                var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!dbs.All(f => ReadAppIds(f.FullName, read))) return null;
                DbPaths[dir] = known = (stamp, read);
            }
            // measured: reading every folder's WAL allocated 13 GB for 14,000 folders
            if (!known.Paths.All(p => IsGameExe(p, g))) return false;
            var paths = new HashSet<string>(known.Paths, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files.Where(f => f.Name.EndsWith(".dxcache-wal", StringComparison.OrdinalIgnoreCase))) ReadWal(f.FullName, paths);
            return paths.Count == 0 ? null : paths.All(p => IsGameExe(p, g));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // folder -> its dbs' names, sizes and write times, and the exe paths they hold; the WAL isn't kept
    // ponytail: never pruned, a deleted folder's entry stays until the app restarts (a few MB at 14,000 folders)
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Stamp, HashSet<string> Paths)> DbPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders in <paramref name="root"/> created since <paramref name="since"/> whose dbs name only exes under
    /// <paramref name="dir"/>; one in doubt (an unreadable db, no path, a creation time not known) is never one. Only those new
    /// folders' dbs are read, none of the folders named in <paramref name="skip"/>. <paramref name="doubt"/> gets the new folders
    /// with a db that couldn't be read.</summary>
    public static IReadOnlyList<string> Made(string root, string dir, DateTime since, IReadOnlySet<string>? skip = null, ICollection<string>? doubt = null) =>
        Made(root, [dir], since, skip, doubt);

    /// <summary><see cref="Made(string, string, DateTime, IReadOnlySet{string}?, ICollection{string}?)"/> with exes under any of <paramref name="dirs"/>.
    /// With <paramref name="game"/>, also one whose dbs name only the game's exe in a folder the runtime didn't name for it: a
    /// staged db named for the game whose move into the game's folder didn't follow.</summary>
    public static IReadOnlyList<string> Made(string root, IReadOnlyList<string> dirs, DateTime since, IReadOnlySet<string>? skip = null, ICollection<string>? doubt = null,
        Game? game = null)
    {
        var unders = dirs.Select(dir => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar).ToList();
        bool Under(string p) => unders.Any(u => p.StartsWith(u, StringComparison.OrdinalIgnoreCase));
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        foreach (var d in new DirectoryInfo(root).EnumerateDirectories().Where(d => FileTimes.CreatedUtc(d) >= since.ToUniversalTime().AddSeconds(-2) && skip?.Contains(d.Name) != true))
            try
            {
                var paths = ExePaths(d.FullName);
                // a damaged db's rows can't be read, its bytes may still name the exe: then the fold finds it damaged
                if (paths == null && doubt != null && RawPaths(d.FullName) is { } raw)
                {
                    // the runtime names a folder by its process's exe path: one of ours, and nothing else's in it
                    if (raw.All(Under) && raw.Any(p => FolderName(p) == d.Name)) found.Add(d.FullName);
                    else doubt.Add(d.FullName);
                }
                else if (paths == null) doubt?.Add(d.FullName);
                else if (paths.Count > 0 && paths.All(p => Under(p) || game != null && IsGameExe(p, game) && FolderName(p) != d.Name)) found.Add(d.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { doubt?.Add(d.FullName); }
        return found;
    }

    /// <summary>Null when a db can't be read.</summary>
    public static HashSet<string>? ExePaths(string dir)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir))
            if (f.EndsWith(".dxcache", StringComparison.OrdinalIgnoreCase))
            {
                if (!ReadAppIds(f, paths)) return null;
            }
            else if (f.EndsWith(".dxcache-wal", StringComparison.OrdinalIgnoreCase)) ReadWal(f, paths);
        return paths;
    }

    /// <summary>The exe paths in the bytes of the folder's dbs and WALs; null when none.</summary>
    static HashSet<string>? RawPaths(string dir)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir))
            if (f.EndsWith(".dxcache", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dxcache-wal", StringComparison.OrdinalIgnoreCase)) ReadWal(f, paths);
        return paths.Count > 0 ? paths : null;
    }

    // read in windows that overlap by SQLite's biggest page: a killed merge leaves a WAL of many GB, past an array's size
    static void ReadWal(string file, HashSet<string> paths)
    {
        const int Window = 16 << 20, Overlap = 1 << 16;
        using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[Math.Min(s.Length, Window + Overlap)];
        var kept = 0;
        for (int n; (n = s.ReadAtLeast(buf.AsSpan(kept), buf.Length - kept, throwOnEndOfStream: false)) > 0;)
        {
            var b = buf.AsSpan(0, kept + n);
            for (int at = 1, i; at < b.Length && (i = b[at..].IndexOf(@":\"u8)) >= 0; at += i + 2)
                if (char.IsAsciiLetter((char)b[at + i - 1]) && RowPath(b, at + i - 1) is { } path) paths.Add(path);
            kept = Math.Min(Overlap, b.Length);
            b[^kept..].CopyTo(buf);
        }
    }

    /// <summary>The exe path an app_id record holds at <paramref name="at"/>: its header ends right there, the id (a rowid alias)
    /// stored as nothing, then exe_path's text type, whose byte count is where the path ends (a folder name may hold ".exe").</summary>
    static string? RowPath(ReadOnlySpan<byte> b, int at)
    {
        for (var h = 3; h <= Math.Min(at, 64); h++)
        {
            if (b[at - h] != h) continue;   // the header's size, a one-byte varint here
            var types = new List<long>();
            for (var i = at - h + 1; i < at;) types.Add(Varint(b, ref i, at));
            if (types.Count < 2 || types.Contains(-1) || types[0] is not (0 or 8 or 9) || types[1] < 13 || types[1] % 2 == 0) continue;
            var n = (int)Math.Min((types[1] - 13) / 2, int.MaxValue);
            if (n > b.Length - at || !System.Text.Unicode.Utf8.IsValid(b.Slice(at, n))) continue;
            var path = Encoding.UTF8.GetString(b.Slice(at, n));   // SQLite text is UTF-8
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !path.Any(char.IsControl)) return path;
        }
        return null;
    }

    /// <summary>SQLite's big-endian varint at <paramref name="i"/>; -1 when it runs to <paramref name="end"/>.</summary>
    static long Varint(ReadOnlySpan<byte> b, ref int i, int end)
    {
        long v = 0;
        for (var k = 0; k < 9; k++)
        {
            if (i >= end) return -1;
            var x = b[i++];
            if (k == 8) return v << 8 | x;
            v = v << 7 | (long)(x & 0x7F);
            if (x < 0x80) return v;
        }
        return v;
    }

    /// <summary>The runtime's files in a cache folder, nothing else it may hold: <c>&lt;GUID&gt;[_VEN_..&amp;DEV_..&amp;SUBSYS_..&amp;REV_..]</c>
    /// with .dxcache (and its -shm, -wal) or the older .idx, .val, .lock.</summary>
    public static IEnumerable<FileInfo> CacheFiles(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles().Where(f => CacheFile.IsMatch(f.Name));

    static readonly Regex CacheFile = new(@"^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}(_VEN_[0-9A-F]+&DEV_[0-9A-F]+&SUBSYS_[0-9A-F]+&REV_[0-9A-F]+)?\.(dxcache(-shm|-wal)?|idx|val|lock)$",
        RegexOptions.IgnoreCase);

    /// <summary>An Xbox game's exe runs from <c>...\WindowsApps\&lt;Name&gt;_&lt;Version&gt;_&lt;Arch&gt;_&lt;ResourceId&gt;_&lt;PublisherId&gt;\&lt;path in Content&gt;</c>:
    /// Name_PublisherId is its package family, any version matches. The folder is named by the path as launched (measured on
    /// 621): through a junction, a subst drive or a mounted folder the same exe gets another one, so the same file counts too.</summary>
    public static bool IsGameExe(string path, Game g)
    {
        if (path.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
        if (Path.GetFileName(path).Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase)
            && Games.GameFiles.Identity(path)?.Id is { } id && Games.GameFiles.Identity(g.ExePath)?.Id is { } own && id.AsSpan().SequenceEqual(own)) return true;
        if (g.Store != Store.Xbox || !g.Id.StartsWith("xbox:", StringComparison.Ordinal)) return false;
        var parts = path.Split('\\');
        var i = Array.FindIndex(parts, p => p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 2 >= parts.Length) return false;
        var pkg = parts[i + 1].Split('_');   // package names have no '_'
        return pkg.Length == 5 && $"{pkg[0]}_{pkg[4]}".Equals(g.Id["xbox:".Length..], StringComparison.OrdinalIgnoreCase)
            && string.Join('\\', parts[(i + 2)..]).Equals(Path.GetRelativePath(g.InstallDir, g.ExePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>False when not a readable SQLite db; one without the table yet (rows still in the WAL) adds nothing.</summary>
    static bool ReadAppIds(string file, HashSet<string> paths)
    {
        var rc = sqlite3_open_v2(Immutable(file), out var db, 0x01 /* READONLY */ | 0x40 /* URI */, 0);
        try
        {
            if (rc != 0) return false;
            long tables = 0;
            if (!Rows(db, "select count(*) from sqlite_master where type='table' and name='app_id'", st => tables = sqlite3_column_int64(st, 0))) return false;
            return tables == 0 || Rows(db, "select exe_path from app_id", st =>
            {
                if (Marshal.PtrToStringUTF8(sqlite3_column_text(st, 0)) is { Length: > 0 } p) paths.Add(p);
            });
        }
        finally { sqlite3_close_v2(db); }
    }

    /// <summary>The folder name the runtime opens for a process started from <paramref name="exePath"/>, exactly as the
    /// process sees it (GetModuleFileNameW, case kept): its CRC-64 over UTF-16LE in lowercase hex, no leading zeros.</summary>
    public static string FolderName(string exePath)
    {
        // D3DSCache.dll GetFilenameInAppDataStorageForCurrentApp: reflected poly 0x92C64265D32139A4, this init, no final xor
        var crc = 0x012345678ABCDEF0UL;
        foreach (var b in MemoryMarshal.AsBytes(exePath.AsSpan()))
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? crc >> 1 ^ 0x92C64265D32139A4UL : crc >> 1;
        }
        return crc.ToString("x");
    }

    /// <summary>The game's folder a compile's dbs go to, with the one exe path its dbs name, which must still exist (an Xbox
    /// game's earlier versions each have a folder too): the one written last.</summary>
    public static (string Dir, string Exe)? GameFolder(string root, Game g)
    {
        foreach (var d in FoldersOf(root, g).Select(d => (Info: new DirectoryInfo(d), Real: Followed(d, out _))).Where(d => d.Real != null)
                     .OrderByDescending(d => new DirectoryInfo(d.Real!).LastWriteTimeUtc).Select(d => d.Info))
            // a staged db named for the game but not moved yet sits in a folder the runtime named for another exe
            if (ExePaths(d.FullName) is { Count: 1 } exe && IsGameExe(exe.Single(), g) && File.Exists(exe.Single()) && FolderName(exe.Single()) == d.Name) return (d.FullName, exe.Single());
        return null;
    }

    /// <summary>The folder the runtime reads and writes for <paramref name="dir"/>: itself, or when it is a link (a user's junction
    /// to another drive) its target, one hop to a real folder outside <paramref name="dir"/>'s parent on a local fixed drive with
    /// no link or mount point on its path. Null with <paramref name="why"/> ("links to ...") otherwise.</summary>
    public static string? Followed(string dir, out string? why)
    {
        why = null;
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists || !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return dir;
            why = "links to a folder that isn't there";
            if (info.LinkTarget is not { } link) return null;
            var target = Path.GetFullPath(link, Path.GetDirectoryName(info.FullName)!);
            why = "links to a network or removable drive";
            if (Path.GetPathRoot(target) is not { Length: 3 } root || !char.IsAsciiLetter(root[0]) || root[1..] != ":\\"
                || new DriveInfo(root).DriveType != DriveType.Fixed) return null;
            why = "links to a folder that isn't there";
            if (!Directory.Exists(target)) return null;
            // a folder beside it may be taken for a staged one, moved and deleted
            why = "links to another folder in D3DSCache";
            if (Path.GetDirectoryName(info.FullName) is { } beside && (Games.GameFiles.Inside(beside, target)
                || Games.GameFiles.FinalPath(beside) is { } real && Games.GameFiles.Inside(real, target))) return null;
            // a chain, a loop, a mount point, a junction or a substituted drive on its path, or a name the link reaches as another
            // folder (a trailing dot): what the link opens must be this very folder, by its final path and its file id
            why = "links to another link";
            for (var p = target; Path.GetDirectoryName(p) is { } parent; p = parent)
                if (File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) return null;
            if (Games.GameFiles.Identity(dir) is not { Id: { } reached, Final: { } final } || Games.GameFiles.Identity(target) is not { Id: { } own }
                || !reached.AsSpan().SequenceEqual(own) || !string.Equals(final, Path.TrimEndingDirectorySeparator(target), StringComparison.OrdinalIgnoreCase)) return null;
            why = null;
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            why = "links to a folder that can't be read";
            return null;
        }
    }

    /// <summary>Puts every db of a staged exe's folder into the game's: NVIDIA from 621 keeps compiled pipelines there, keyed by
    /// content, so they hit under any exe path. A missing db is copied in naming <paramref name="exe"/>; an existing one gains
    /// the missing rows in one transaction, only with the same schema and compiler, both dbs checked by SQLite first unless
    /// none is missing. False on doubt, that db unchanged then: a db held open by any process, another compiler, a db that
    /// fails SQLite's check, a link. <paramref name="stop"/> true (the
    /// game started) ends it early: the game's db is rolled back and released, the rest left as it is. <paramref name="refused"/>
    /// gets why each db stayed out. The adapter dbs take the identity <see cref="Decide"/> gives (an Xbox game's
    /// <paramref name="package"/> family), which <paramref name="stamped"/> gets when an adapter db went in or all stayed out for it.
    /// <paramref name="progress"/> gets the units of <see cref="MergeWork"/> done as they go. <paramref name="checkedDbs"/>: the game's
    /// dbs by path as <see cref="CheckStamp"/> read them after the last check or write of ours; one still the same skips its check.</summary>
    public static bool MergeInto(string from, string to, string exe, Action<long>? added = null, Func<bool>? stop = null, Action<string>? refused = null,
        string? package = null, Action<IdentityStamp>? stamped = null, Action<long>? progress = null, IDictionary<string, string>? checkedDbs = null,
        Action<bool>? past = null)
    {
        var ok = true;
        Lookup(exe, out var read);
        var decided = Decide(read, exe, package, Planning.RulesFile.Current);
        if (decided.Source == IdentityStamp.Refused)
        {
            stamped?.Invoke(decided);
            refused?.Invoke("the game's shader delivery registrations differ");
            return false;
        }
        var into = false;
        foreach (var f in Directory.GetFiles(from, "*.dxcache"))
        {
            string? why;
            try { why = stop?.Invoke() == true ? GameStarted : MergeDb(f, Path.Combine(to, Path.GetFileName(f)), exe, added, stop, decided.Stamped,
                decided.Source == IdentityStamp.ByRule, progress, checkedDbs, past); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = e.Message; }
            into |= why == null && Path.GetFileName(f).Contains("_VEN_", StringComparison.OrdinalIgnoreCase);
            if (why == null) continue;
            refused?.Invoke(why);
            ok = false;
        }
        if (into) stamped?.Invoke(decided);
        return ok;
    }

    /// <summary>The folder's dbs that fail SQLite's check, or whose keys and values can't be read as the merge counts them: what the
    /// compile wrote there is lost. <paramref name="doubt"/> gets why a db couldn't be checked (held, unopenable, a read that
    /// failed), which is never damage.</summary>
    public static List<string> Damaged(string dir, out string? doubt)
    {
        var (bad, why) = (new List<string>(), (string?)null);
        foreach (var f in Directory.GetFiles(dir, "*.dxcache"))
        {
            var name = Path.GetFileName(f);
            if (Held(f))
            {
                why ??= $"{name} is open in another process";
                continue;
            }
            if (sqlite3_open_v2(f, out var db, 0x01 /* READONLY */, 0) != 0)
            {
                sqlite3_close_v2(db);
                why ??= $"{name} can't be opened";
                continue;
            }
            try
            {
                string? check = null, tables = null;
                var rc = Step(db, "pragma quick_check", st => check = Marshal.PtrToStringUTF8(sqlite3_column_text(st, 0)));
                if (rc == Done) rc = Step(db, "select count(*) from sqlite_master where name in ('keys', 'vals')", st => tables = Marshal.PtrToStringUTF8(sqlite3_column_text(st, 0)));
                if (rc == Done && tables == "2") rc = Step(db, "select count(*) from keys k join vals v on v.keyid = k.keyid", _ => { });
                if (rc == Done && check == "ok") continue;
                if (rc == Done || (rc & 0xFF) is 11 /* CORRUPT */ or 26 /* NOTADB */) bad.Add(f);
                else why ??= $"{name} couldn't be read ({Marshal.PtrToStringUTF8(sqlite3_errstr(rc))})";
            }
            finally { sqlite3_close_v2(db); }
        }
        doubt = why;
        return bad;
    }

    /// <summary>The adapter dbs of <paramref name="a"/> that name another compiler where the same db of <paramref name="b"/> names
    /// <paramref name="current"/> (this driver's); none on doubt.</summary>
    public static List<string> OtherCompiler(string a, string b, string current) =>
        [.. Directory.GetFiles(a, "*_VEN_*.dxcache").Where(f => Path.Combine(b, Path.GetFileName(f)) is var other && File.Exists(other)
            && !LinkedDb(f) && !LinkedDb(other) && Compiler(f) is { Length: > 0 } x && Compiler(other) == current && x != current)];

    static string? Compiler(string file)
    {
        if (sqlite3_open_v2(file, out var db, 0x01 /* READONLY */, 0) != 0) { sqlite3_close_v2(db); return null; }
        try { return Text(db, string.Format(CompilerSql, "main")); }
        finally { sqlite3_close_v2(db); }
    }

    const string SchemaSql = "select group_concat(name || ':' || sql, '|') from (select name, sql from {0}.sqlite_master order by name)";
    const string CompilerSql = "select group_concat(abi || ':' || version || ':' || family, '|') from (select * from {0}.compiler_id order by id)";

    const string GameStarted = "the game started";

    /// <summary>A <see cref="MergeInto"/> refusal of the compile's db itself, after which its folder is checked for damage.</summary>
    public static bool OwnDbRefusal(string why) => why.StartsWith("the compile's db", StringComparison.Ordinal) && why != "the compile's db is open in another process"
        || why == "the rows couldn't be written";

    /// <summary>Free space a fold or a warm leaves on a volume: below it a fold stops and a warm pauses. Windows and the
    /// driver's cache write at a warm's exit need room.</summary>
    public const long DiskFloor = 10L << 30;

    const string NoRoomWhy = "not enough free disk space";

    /// <summary>A <see cref="MergeInto"/> refusal for free space: the dbs stay where they are, nothing was written.</summary>
    public static bool NoRoom(string why) => why.StartsWith(NoRoomWhy, StringComparison.Ordinal);

    /// <summary>What a merge of the staged folder <paramref name="dir"/> needs free: its biggest db twice (the game db's WAL,
    /// then the checkpoint into the db) and <see cref="DiskFloor"/>.</summary>
    public static long RoomFor(string dir) => 2 * CacheFiles(dir).Select(f => f.Length).DefaultIfEmpty().Max() + DiskFloor;

    // the insert's WAL and then its checkpoint each take about the staged db again, a copy to another volume takes it once
    static string? Room(string from, string to, bool copy, long size)
    {
        var need = !copy ? 2 * size + DiskFloor : SameVolume(from, to) ? 0 : size + DiskFloor;
        return need > 0 && FreeBytes(to) is { } free && free < need ? $"{NoRoomWhy}: it needs {Format.Bytes(need)} free on {Drive(to)}" : null;
    }

    public static string Drive(string path) => Volume(path)?.TrimEnd('\\') ?? path;

    // a Unix path's root is always "/": Linux asks the mount (links followed), macOS has no statx
    static string? Volume(string path) => OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(path))
        : OperatingSystem.IsLinux() ? Games.GameFiles.VolumeOf(Path.GetFullPath(path)) : null;

    /// <summary>Free bytes on <paramref name="path"/>'s volume, null when unknown. SCSKILLER_TEST_DISK_FREE names a file whose
    /// number stands for every volume's, read at each call (E2E).</summary>
    public static long? FreeBytes(string path)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("SCSKILLER_TEST_DISK_FREE") is { Length: > 0 } test)
                return long.TryParse(File.ReadAllText(test).Trim(), out var n) ? n : null;
            if (OperatingSystem.IsWindows()) return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
            // statvfs of the nearest folder that exists, links followed
            var dir = Path.GetFullPath(path);
            while (!Directory.Exists(dir) && Path.GetDirectoryName(dir) is { } up) dir = up;
            return new DriveInfo(dir).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>A <see cref="MergeInto"/> refusal read from the dbs' content alone, under their locks: it stands until a db changes.
    /// One of a lock, an open, an identity or a write may pass without any.</summary>
    public static bool Lasting(string why) => why is "the game's db has another schema" or "the game's db names another compiler" or "the game's db names another exe";

    /// <summary>Null when merged, else why not.</summary>
    static string? MergeDb(string from, string to, string exe, Action<long>? added, Func<bool>? stop, AppIdentity? asd = null, bool byRule = false,
        Action<long>? progress = null, IDictionary<string, string>? checkedDbs = null, Action<bool>? past = null)
    {
        // the runtime stamps a registered exe's identity into its adapter db only, and empties an adapter db without it
        var adapter = Path.GetFileName(from).Contains("_VEN_", StringComparison.OrdinalIgnoreCase);
        var stamp = adapter ? asd : null;
        if (!CacheFile.IsMatch(Path.GetFileName(from))) return "not a runtime cache file";
        if (LinkedDb(from) || LinkedDb(to)) return "a db is a link";
        if (Held(from) || Held(from + "-wal")) return "the compile's db is open in another process";
        DropTmp(to);
        var copy = !File.Exists(to);
        var (size, g) = (new FileInfo(from).Length, copy ? 0 : new FileInfo(to).Length);
        var work = Work(size, copy ? null : g, SameVolume(from, to), !copy && FullDue(checkedDbs, to));
        long reported = 0;
        var gate = new Lock();
        void Advance(long to)
        {
            lock (gate)
                if (to > reported)
                {
                    progress?.Invoke(to - reported);
                    reported = to;
                }
        }
        // SQLite reports nothing inside a check or an insert: this process's reads since the step began stand for its progress
        T Step<T>(long bytes, Func<T> run)
        {
            var (at, start) = (reported, ReadBytes());
            var (ended, over) = (false, false);
            T result;
            using (progress == null ? null : new Timer(_ =>
            {
                lock (gate)
                {
                    if (ended) return;
                    var read = ReadBytes() - start;
                    Advance(at + Math.Clamp(read, 0, bytes));
                    // past its estimate the step's share can't be told: shown as writing until it ends
                    if (read >= bytes && !over) try { past?.Invoke(over = true); } catch (Exception) { }   // a timer thread's throw would end the app
                }
            }, null, 1000, 1000))
                try { result = run(); }
                finally
                {
                    lock (gate)
                    {
                        ended = true;
                        // a display callback that throws never changes the merge's path
                        if (over) try { past?.Invoke(false); } catch (Exception) { }
                    }
                }
            Advance(at + bytes);
            return result;
        }
        // the game started: the next step fails with SQLITE_INTERRUPT, which rolls the transaction back and frees the db
        // an exception can't cross SQLite's native frames: a check that throws lets the merge go on
        var stopped = false;
        Progress? check = stop == null ? null : _ =>
        {
            try { return (stopped |= stop()) ? 1 : 0; }
            catch (Exception) { return 0; }
        };
        Func<bool>? halt = check == null ? null : () => check(0) != 0;
        if (!Open(from, out var src, uri: true)) return "the compile's db can't be opened";
        if (check != null) sqlite3_progress_handler(src, 10000, check, 0);
        long rows = 0;
        (long Keys, long Vals)? lacking = null;
        try
        {
            long busy = -1;
            // the copy takes the main file only: every WAL frame must be in it
            if (copy && (!Rows(src, "pragma wal_checkpoint(TRUNCATE)", st => busy = sqlite3_column_int64(st, 0)) || busy != 0)) return "the compile's db WAL can't be checkpointed";
            if (Text(src, string.Format(SchemaSql, "main")) is not { } schema
                || !schema.Contains("keys:") || !schema.Contains("vals:") || !schema.Contains("app_id:")) return "the compile's db fails SQLite's check";
            // read without the game's db lock, which a check of a big db would hold for minutes
            if (!copy) lacking = Exec(src, $"attach database {Quote(Immutable(to))} as g") ? Lacking(src, "main", "g") : null;
            // the db as it is now, its WAL's rows included: a copy checkpointed them into it above
            if (lacking is not (0, 0) && Room(from, to, copy, new FileInfo(from).Length + (File.Exists(from + "-wal") ? new FileInfo(from + "-wal").Length : 0)) is { } room) return room;
            if (lacking is not (0, 0) && Step(size, () => QuickCheck(src, "main", from, halt)) != "ok") return stop?.Invoke() == true ? GameStarted : "the compile's db fails SQLite's check";
            Rows(src, "select count(*) from keys", st => rows = sqlite3_column_int64(st, 0));
        }
        finally
        {
            sqlite3_close_v2(src);
            GC.KeepAlive(check);
        }
        if (copy)
        {
            checkedDbs?.Remove(to + HeldKey);   // a refusal's evidence was of a db that is gone
            if (File.Exists(to + "-wal") || File.Exists(from + "-wal") && new FileInfo(from + "-wal").Length > 0) return "a WAL is left beside a db";   // a WAL would replay into the copy
            if (SameVolume(from, to))
            {
                // one volume: the staged db (ours) is named for the game where it is, then renamed into the game's folder
                if (stop?.Invoke() == true) return GameStarted;
                if (!Open(from, out var own)) return "the compile's db can't be opened";
                try
                {
                    if (!NameFor(own, exe)) return "the compile's db can't be named for the game";
                    // a staged db left pending carries the stamp of the fold before: each copy takes this one's, none included
                    if (adapter && !Stamp(own, stamp ?? AppIdentity.None)) return "the compile's db can't take the game's shader delivery identity";
                }
                finally { sqlite3_close_v2(own); }   // the last connection checkpoints into the file
                TestPause();
                if (File.Exists(from + "-wal") || Held(from)) return "the compile's db is open in another process";
                if (stop?.Invoke() == true) return GameStarted;
                File.Move(from, to);   // fails when the runtime made one meanwhile: merged next time
                File.Delete(from + "-shm");
                added?.Invoke(rows);
                Advance(work);
                return null;
            }
            var tmp = to + "." + Guid.NewGuid().ToString("N") + ".scskiller-tmp";
            FileStream? mine = null;   // held until the rename: another process's DropTmp can't delete it meanwhile
            try
            {
                // no other writer while the bytes are read: the checkpointed file is the whole db
                using (var s = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var d = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (File.Exists(from + "-wal") && new FileInfo(from + "-wal").Length > 0) return "a WAL is left beside a db";
                    var buffer = new byte[1 << 20];
                    long copied = 0;
                    for (int n; (n = s.Read(buffer)) > 0;)
                    {
                        d.Write(buffer, 0, n);
                        Advance(size + (long)(size * Math.Min(1.0, (copied += n) / (double)Math.Max(1, s.Length))));   // the checkpoint may have grown it past size
                    }
                }
                mine = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (!Open(tmp, out var db)) return "the copied db can't be opened";
                try
                {
                    if (!NameFor(db, exe)) return "the compile's db can't be named for the game";
                    if (adapter && !Stamp(db, stamp ?? AppIdentity.None)) return "the compile's db can't take the game's shader delivery identity";
                }
                finally { sqlite3_close_v2(db); }   // the last connection checkpoints into tmp
                if (File.Exists(tmp + "-wal")) return "a WAL is left beside a db";
                if (stop?.Invoke() == true) return GameStarted;
                mine.Dispose();
                File.Move(tmp, to);   // fails when the runtime made one meanwhile: merged next time
                added?.Invoke(rows);
                Advance(work);
                return null;
            }
            finally
            {
                mine?.Dispose();
                File.Delete(tmp);
                File.Delete(tmp + "-wal");
                File.Delete(tmp + "-shm");
            }
        }
        if (Held(to) || Held(to + "-wal")) return "the game's db is open in another process";
        // read before this connection locks the db, whose lock bytes a big db's stamp may cover; WAL frames are a write it can't see
        var wal = File.Exists(to + "-wal") && new FileInfo(to + "-wal").Length > 0;
        var seen = wal ? null : CheckStamp(to);
        if (!Open(to, out var t)) return "the game's db can't be opened";
        string? refusedAs = null;
        if (check != null) sqlite3_progress_handler(t, 10000, check, 0);
        try
        {
            // the db's EXCLUSIVE lock, taken at the first read, fails while any other connection holds its SHARED lock (all of
            // them do in WAL mode): a game that opened it after Held keeps it, and none reads or writes it until this one closes.
            // No busy timeout: the dbs wait for the next try.
            TestPause();
            if (!Exec(t, "pragma main.locking_mode=exclusive") || !Exec(t, "select count(*) from main.sqlite_master")) return "the game's db is in use by another process";
            // read under the exclusive lock: nothing writes the db until it closes
            var known = checkedDbs != null && checkedDbs.TryGetValue(to, out var was) && checkedDbs.Remove(to) && was == CheckStamp(to);
            if (!Exec(t, $"attach database {Quote(from)} as s")) return "the compile's db can't be attached";
            var schema = Text(t, string.Format(SchemaSql, "main"));
            if (schema == null || schema != Text(t, string.Format(SchemaSql, "s"))) return "the game's db has another schema";
            if (schema.Contains("compiler_id:") && (Text(t, string.Format(CompilerSql, "main")) is not { Length: > 0 } mine || mine != Text(t, string.Format(CompilerSql, "s")))) return "the game's db names another compiler";
            if (stop?.Invoke() == true) return GameStarted;
            if (!Exec(t, "begin immediate")) return "the game's db is locked";
            // under the lock again: the game may have changed its db since; nothing to add skips the checks and the inserts
            var now = Lacking(t, "s", "main");
            if (stop?.Invoke() == true)
            {
                Rollback(t);
                return GameStarted;
            }
            var write = now is not (0, 0);
            string? refused = null;
            if (write && lacking is (0, 0) && Step(size, () => QuickCheck(t, "s", from, halt)) != "ok") refused = "the compile's db fails SQLite's check";   // unchecked before: nothing was missing then
            // a full check reads 1.2x the db, every page cold: between full checks (FullDue) only its size and the keys' b-trees,
            // which the insert searches, and the rows it writes are read back in full before the commit. A db with free pages or
            // auto-vacuum gets the full check: an insert reuses those pages or writes pointer maps, and only it knows none is in use.
            // decided under the lock: the db checked whole may have been replaced since the estimate. A due one isn't skipped as known.
            var full = FullDue(checkedDbs, to) || FreePages(t) != 0 || Text(t, "pragma main.auto_vacuum") != "0";
            if (write && refused == null && (full || !known))
            {
                if (full && Step(g, () => QuickCheck(t, "main", to, halt)) == "ok")
                {
                    if (checkedDbs != null && FullStamp(to, DateTime.UtcNow) is { } at) checkedDbs[to + FullKey] = at;
                }
                else if (full || !Sane(t, to, wal)) refused = "the game's db fails SQLite's check";
            }
            var other = stamp != null && !byRule && Text(t, $"select count(*) from main.app_id where not (app_name is null and engine_name is null) and not ({stamp.Is})") != "0";
            if (!other) checkedDbs?.Remove(to + HeldKey);
            var ids = other ? Text(t, IdentitiesSql) : null;
            var held = ids != null ? $"{ids}\n{Shown(stamp!)}\n" : null;
            // the runtime wrote the db since it was refused and left it that identity: the identity it keeps, so the rows go in under it
            var kept = held != null && checkedDbs != null && checkedDbs.TryGetValue(to + HeldKey, out var h) && h.StartsWith(held, StringComparison.Ordinal)
                && (wal || seen != null && h[held.Length..] != seen);
            refused ??= Text(t, $"select count(*) from main.app_id where exe_path is not {Quote(exe)}") != "0" ? "the game's db names another exe"
                // made under an earlier registration: the game's next start empties it, the rows go in after that
                // a rule never changes a db the game's runtime wrote: its rows go in only where it holds the rule's identity
                : stamp != null && byRule && Text(t, $"select count(*) from main.app_id where not ({stamp.Is})") != "0" ? "the game's db holds another identity than its rule's"
                : other && !kept ? $"{FromBefore} (it holds {ids ?? "an unreadable identity"}, the registration {Shown(stamp!)})"
                : stamp != null && !byRule && !other && !Stamp(t, stamp) ? "the game's db can't take its shader delivery identity" : null;
            if (refused != null)
            {
                if (!stopped && refused is "the game's db fails SQLite's check" or "the game's db can't take its shader delivery identity") FullAgain(checkedDbs, to);
                if (other && !kept) refusedAs = held;
                Rollback(t);
                return refused;
            }
            var fresh = Fresh("s", "main");
            rows = 0;
            long last = 0;
            if (write) Rows(t, $"select count(*) from {fresh}", st => rows = sqlite3_column_int64(st, 0));
            long key = 0;
            if (write) Rows(t, "select coalesce(max(rowid), 0) from main.vals", st => last = sqlite3_column_int64(st, 0));
            if (write) Rows(t, "select coalesce(max(keyid), 0) from main.keys", st => key = sqlite3_column_int64(st, 0));
            // groups stay out: no driver writes them yet. An insert whose select reads main.vals copies its rows to a temp file
            // first: the new keys' values, the bulk, go in without that read
            var wrote = !write || Step(size, () => Exec(t, $"insert into main.keys(key, last_accessed) select k.key, k.last_accessed from {fresh}")
                && Exec(t, "insert into main.vals(keyid, type, value) select m.keyid, v.type, v.value from main.keys m join s.keys k on k.key = m.key "
                    + $"join s.vals v on v.keyid = k.keyid where m.keyid > {key}")
                && Exec(t, "insert into main.vals(keyid, type, value) select m.keyid, v.type, v.value from s.keys k join s.vals v on v.keyid = k.keyid "
                    + $"join main.keys m on m.key = k.key where m.keyid <= {key} and not exists (select 1 from main.vals x where x.keyid = m.keyid and x.type = v.type)")
                && Text(t, string.Format(ReadBackSql, last)) is { } back && back == Text(t, StagedRowsSql));
            var halted = wrote && stop?.Invoke() == true;
            if (wrote && !halted && Step(0, () => Exec(t, "commit")))
            {
                // checked (or skipped as unchanged) and written by us alone: the stamp after the checkpoint stands for that state
                if (checkedDbs != null && (write || known) && Step(0, () => Rows(t, "pragma main.wal_checkpoint(TRUNCATE)", st => { })) && CheckStamp(to) is { } after)
                    checkedDbs[to] = after;
                added?.Invoke(rows);
                Advance(work);
                return null;
            }
            Rollback(t);
            // a write or read-back that failed on the game's own pages, not a stop: a full check next time, even if the game starts now
            if (!halted && !stopped) FullAgain(checkedDbs, to);
            if (stop?.Invoke() == true) return GameStarted;
            // the verdict of the check it skipped
            if (check != null) sqlite3_progress_handler(t, 10000, check, 0);
            var verdict = Step(g, () => QuickCheck(t, "main", to, halt));
            return stop?.Invoke() == true ? GameStarted : verdict != "ok" ? "the game's db fails SQLite's check" : "the rows couldn't be written";
        }
        finally
        {
            sqlite3_close_v2(t);
            GC.KeepAlive(check);
            // the db as the refusal left it, after the close's checkpoint: a later change is the runtime's
            if (refusedAs != null && CheckStamp(to) is { } left) checkedDbs![to + HeldKey] = refusedAs + left;
        }
    }

    const string FromBefore = "the game's db is from before its shader delivery";

    /// <summary>SQLite's check of <paramref name="file"/>, attached as <paramref name="schema"/>, after one sequential read of it.</summary>
    // measured: SQLite reads a cold db a page at a time, about 60 MB/s on NVMe, a sequential read fills the file cache at disk speed
    // ponytail: a db bigger than free memory keeps little of it, the read then costs one pass
    static string? QuickCheck(nint db, string schema, string file, Func<bool>? stop)
    {
        try
        {
            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var buffer = new byte[4 << 20];
            while (stop?.Invoke() != true)
                try
                {
                    if (s.Read(buffer) == 0) break;
                }
                catch (IOException) { s.Position += buffer.Length; }   // SQLite's lock bytes at 1 GB, under this merge's own lock
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // SQLite's own read decides
        // its check of a cold db sees a stop only every 10,000 pages
        return stop?.Invoke() == true ? null : Text(db, $"pragma {schema}.quick_check");
    }

    static long FreePages(nint t) => long.TryParse(Text(t, "pragma main.freelist_count"), out var n) ? n : -1;

    /// <summary>Under <c>"&lt;path&gt;|full"</c> in <c>checkedDbs</c>: the db's file id and when its last full check passed.</summary>
    const string FullKey = "|full";

    static readonly TimeSpan FullEvery = TimeSpan.FromDays(7);

    /// <summary>The game's db gets SQLite's full check: never checked whole, another file now (a new or replaced db), 7 days
    /// since, or the last merge into it failed a check or rolled back (its entry dropped).</summary>
    static bool FullDue(IEnumerable<KeyValuePair<string, string>>? checkedDbs, string db)
    {
        var now = DateTime.UtcNow;
        // a time ahead of the clock (it was set back) counts as due too
        return checkedDbs?.FirstOrDefault(e => e.Key.Equals(db + FullKey, StringComparison.OrdinalIgnoreCase)).Value?.Split('|') is not [var id, var ticks]
            || FullStamp(db, now)?.Split('|')[0] != id || !long.TryParse(ticks, out var at) || at > now.Ticks || at <= (now - FullEvery).Ticks;
    }

    /// <summary>A "|full" entry that asks for the full check again: each one unique, so another process's fold that checked the
    /// db earlier can't save its pass over it.</summary>
    public const string Again = "again ";

    static void FullAgain(IDictionary<string, string>? checkedDbs, string db)
    {
        if (checkedDbs != null) checkedDbs[db + FullKey] = Again + Guid.NewGuid().ToString("N");
    }

    internal static string? FullStamp(string db, DateTime at) => Games.GameFiles.Identity(db)?.Id is { } id ? $"{Convert.ToHexString(id)}|{at.Ticks}" : null;

    /// <summary>A copy to another volume killed mid-way leaves its temp file beside the db: ours by name, and old enough that no
    /// copy still writes it.</summary>
    static void DropTmp(string db)
    {
        if (Path.GetDirectoryName(db) is not { } dir || !Directory.Exists(dir)) return;
        var ours = new Regex("^" + Regex.Escape(Path.GetFileName(db)) + @"\.[0-9a-f]{32}\.scskiller-tmp(-wal|-shm)?$", RegexOptions.IgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(db) + ".*.scskiller-tmp*"))
            if (ours.IsMatch(Path.GetFileName(f)) && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddMinutes(-10))
                try { File.Delete(f); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The game's db is as long as its pages (a WAL may hold more), and its keys table and indexes pass SQLite's check.</summary>
    static bool Sane(nint t, string to, bool wal) =>
        long.TryParse(Text(t, "pragma main.page_size"), out var page) && long.TryParse(Text(t, "pragma main.page_count"), out var pages) && page > 0
        && new FileInfo(to).Length is var n && n % page == 0 && (wal ? n <= pages * page : n == pages * page)
        && Text(t, "pragma main.quick_check(keys)") == "ok";

    // the staged rows as the game's db holds them after the insert, each value read whole: ours (a row after {0}, the game's
    // last before the insert) the staged one, the game's own readable to its last byte; then all of the staged rows
    const string StagedRowsSql = "select count(*) from s.keys k cross join s.vals sv on sv.keyid = k.keyid where k.key is not null";
    const string ReadBackSql = "select count(*) from s.keys k cross join s.vals sv on sv.keyid = k.keyid cross join main.keys m on m.key = k.key "
        + "cross join main.vals v on v.keyid = m.keyid and v.type = sv.type where k.key is not null "
        + "and (v.rowid > {0} and v.value is sv.value or v.rowid <= {0} and (v.value is null or substr(v.value, -1) is not null))";

    /// <summary>A refused db's <see cref="IdentitiesSql"/>, the stamp it was refused for and its <see cref="CheckStamp"/>, in
    /// <c>checkedDbs</c> under its path and this.</summary>
    const string HeldKey = "|identity";

    /// <summary>An identity as SQLite's quote() shows it, as <see cref="IdentitiesSql"/> lists them.</summary>
    static string Shown(AppIdentity a) => $"{Quoted(a.Name)},{Quoted(a.Engine)},{a.Version},{a.EngineVersion}";

    static string Quoted(string? s) => s == null ? "NULL" : "'" + s.Replace("'", "''") + "'";

    const string IdentitiesSql = "select group_concat(i, '|') from (select distinct quote(app_name) || ',' || quote(engine_name) || ',' || quote(app_version) "
        + "|| ',' || quote(engine_version) as i from main.app_id order by i)";

    /// <summary>A merge's progress units for a staged db of <paramref name="size"/> bytes: its check, the game's db's full check
    /// when due (<paramref name="game"/> bytes, null when there is none and the db is copied in) and the inserts, or the copy's
    /// bytes to another volume.</summary>
    static long Work(long size, long? game, bool sameVolume, bool full) => game is { } g ? 2 * size + (full ? g : 0) : sameVolume ? size : 2 * size;

    internal static bool SameVolume(string a, string b) => Volume(a) is { } v
        && string.Equals(v, Volume(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The progress units <see cref="MergeInto"/> reports for the staged folder <paramref name="from"/> going into
    /// <paramref name="to"/> (null: no folder of the game's yet), with the full checks due by <paramref name="checkedDbs"/>.</summary>
    public static long MergeWork(string from, string? to, IEnumerable<KeyValuePair<string, string>>? checkedDbs = null)
    {
        try
        {
            return new DirectoryInfo(from).EnumerateFiles("*.dxcache").Sum(f => Work(f.Length,
                to != null && new FileInfo(Path.Combine(to, f.Name)) is { Exists: true } g ? g.Length : null, to == null || SameVolume(from, Followed(to, out _) ?? to),
                to != null && FullDue(checkedDbs, Path.Combine(Followed(to, out _) ?? to, f.Name))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    static long ReadBytes() => GetProcessIoCounters(GetCurrentProcess(), out var c) ? (long)c.ReadTransferCount : 0;

    /// <summary>How many keys of <paramref name="from"/> <paramref name="into"/> lacks, and values of keys both hold; null
    /// when they can't be read.</summary>
    static (long Keys, long Vals)? Lacking(nint db, string from, string into)
    {
        long keys = -1, vals = -1;
        return Rows(db, $"select count(*) from {Fresh(from, into)}", st => keys = sqlite3_column_int64(st, 0))
            && Rows(db, $"select count(*) from {from}.keys k join {from}.vals v on v.keyid = k.keyid join {into}.keys m on m.key = k.key "
                + $"where not exists (select 1 from {into}.vals x where x.keyid = m.keyid and x.type = v.type)", st => vals = sqlite3_column_int64(st, 0))
            && keys >= 0 && vals >= 0 ? (keys, vals) : null;
    }

    /// <summary>The keys of <paramref name="from"/> <paramref name="into"/> lacks; a NULL key is none.</summary>
    static string Fresh(string from, string into) => $"{from}.keys k where k.key is not null and not exists (select 1 from {into}.keys x where x.key = k.key)";

    static void Rollback(nint db)
    {
        sqlite3_progress_handler(db, 0, null, 0);
        Exec(db, "rollback");
    }

    delegate int Progress(nint arg);

    /// <summary>Every app_id row names <paramref name="exe"/>, and there is one.</summary>
    static bool NameFor(nint db, string exe) => Exec(db, $"update app_id set exe_path = {Quote(exe)}")
        && Text(db, $"select count(*) from app_id where exe_path is not {Quote(exe)}") == "0" && Text(db, "select count(*) from app_id") is { } n && n != "0";

    /// <summary>An exe's Advanced Shader Delivery registration (a store's or installer's, under DirectX\ShaderCache): the identity
    /// the runtime writes into the exe's adapter db, and the precompiled shader dbs delivered for it (their size is shown).</summary>
    public sealed record Delivery(string Name, string Engine, long Version, long EngineVersion, IReadOnlyList<string> Psdbs, IReadOnlyList<string>? Families = null)
    {
        // measured: the runtime writes an empty EngineName as NULL
        public AppIdentity Identity => new(Name, Engine is "" ? null : Engine, Version, EngineVersion);

        /// <summary>The identity, and each adapter family's delivered db with its length and write time: another one is
        /// another delivery.</summary>
        public string Stamp => string.Join('|', new[] { $"{Name}:{Engine}:{Version}:{EngineVersion}" }.Concat(Psdbs.Select((p, i) =>
        {
            var family = Families?.ElementAtOrDefault(i);
            try { return new FileInfo(p) is { Exists: true } f ? $"{family}={p}:{f.Length}:{f.LastWriteTimeUtc.Ticks}" : $"{family}={p}:-"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"{family}={p}:?"; }
        })));

        public long Bytes => Psdbs.Sum(p =>
        {
            try { return new FileInfo(p).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }   // replaced by its store meanwhile
        });
    }

    const string WarmInstaller = "SCSKiller-Warm-", WarmOwner = "SCSKillerOwner";

    /// <summary>Removes the delivery registrations scskiller_warm made for its staged exe (HKCU, installer SCSKiller-Warm-&lt;its
    /// pid&gt;, its start time in SCSKillerOwner) whose process is gone: a warm ended from outside never removes its own. Never
    /// touches another installer's.</summary>
    public static void SweepWarmRegistrations()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\ShaderCache", true);
            foreach (var name in root?.GetSubKeyNames() ?? [])
                if (name.StartsWith(WarmInstaller, StringComparison.Ordinal) && int.TryParse(name.AsSpan(WarmInstaller.Length), System.Globalization.NumberStyles.None, null, out var pid)
                    && !WarmAlive(pid, root!, name))
                    root!.DeleteSubKeyTree(name, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Warn?.Invoke($"a compile's shader delivery registration can't be removed: {e.Message}");
        }
    }

    static bool WarmAlive(int pid, Microsoft.Win32.RegistryKey root, string name)
    {
        try
        {
            using var key = root.OpenSubKey(name);
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            // no start time yet: written just after the key, so a warm by that id may be writing it
            return p.ProcessName.StartsWith("scskiller_warm", StringComparison.OrdinalIgnoreCase)
                && (key?.GetValue(WarmOwner) is not long started || p.StartTime.ToFileTimeUtc() == started);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Windows' automatic disk cleanup empties D3DSCache: its "D3D Shader Cache" handler has Autorun 1 and Storage
    /// Sense is on (its policy, else the user's switch). Only read, never written. SCSKILLER_TEST_D3DSCACHE_CLEANUP: a test
    /// run's stand-in, 1 on, anything else off.</summary>
    public static bool CleanedAutomatically()
    {
        if (Environment.GetEnvironmentVariable("SCSKILLER_TEST_D3DSCACHE_CLEANUP") is { Length: > 0 } test) return test == "1";
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var handler = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(CleanupHandler);
            using var policy = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\StorageSense");
            using var sense = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy");
            // the SilentCleanup maintenance task runs cleanmgr /autocleanstoragesense: Autorun puts the handler in it, Storage Sense runs it (the task's state needs admin)
            return handler?.GetValue("Autorun") is 1 && (policy?.GetValue("AllowStorageSenseGlobal") ?? sense?.GetValue("01")) is 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    // the only cleanup handler that empties D3DSCache (its subfolders), NVIDIA's and AMD's folders have none
    const string CleanupHandler = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\D3D Shader Cache";
    const string KeptAutorun = "SCSKillerAutorun";   // Autorun before KeepFromCleanup(true), -1 = none

    /// <summary>Windows' automatic cleanup leaves D3DSCache alone: its handler's Autorun is 0 (a manual Disk Cleanup still
    /// offers it). Null: no handler, unreadable, or not Windows.</summary>
    public static bool? CleanupKept()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var handler = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(CleanupHandler);
            return handler == null ? null : handler.GetValue("Autorun") is 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    /// <summary>Keeps Windows' automatic cleanup (Storage Sense, the maintenance task) from emptying D3DSCache: Autorun 0, the
    /// value before it kept beside it. False puts that value back (1, Windows' default, when none was kept). Needs admin.
    /// Returns the outcome line, throws when the value read back differs.</summary>
    public static string KeepFromCleanup(bool keep)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows' disk cleanup is Windows only");
        using var handler = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(CleanupHandler, true)
            ?? throw new InvalidOperationException("Windows' disk cleanup has no shader cache entry on this PC");
        var now = handler.GetValue("Autorun");
        int? want = 0;
        if (keep)
        {
            if (now is not 0)
            {
                if (now is not (null or int) || Equals(now, -1)) throw new InvalidOperationException($"Windows' disk cleanup setting holds an unexpected value ({now}), left as it is");
                if (handler.GetValue(KeptAutorun) is not int) handler.SetValue(KeptAutorun, now is int n ? n : -1, Microsoft.Win32.RegistryValueKind.DWord);
                handler.SetValue("Autorun", 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
        }
        else
        {
            want = handler.GetValue(KeptAutorun) switch { -1 => null, int n => n, _ => 1 };
            if (want is { } w) handler.SetValue("Autorun", w, Microsoft.Win32.RegistryValueKind.DWord);
            else handler.DeleteValue("Autorun", false);
        }
        if (handler.GetValue("Autorun") is var after && (after as int?) != want)
            throw new InvalidOperationException($"Windows' disk cleanup setting reads {after ?? "nothing"} after the change, not {want?.ToString() ?? "nothing"}");
        if (!keep) handler.DeleteValue(KeptAutorun, false);
        return keep ? "Windows' automatic disk cleanup no longer clears compiled shaders." : "Windows' automatic disk cleanup can clear compiled shaders again.";
    }

    /// <summary>The registration the runtime applies to <paramref name="exe"/> (HKLM and HKCU): its ExePath is the exe's final
    /// path. Null when none, unreadable, or two differ.</summary>
    public static Delivery? Delivered(string exe) => Lookup(exe, out _);

    /// <summary><see cref="Delivered"/>, with what the read found (<paramref name="read"/>): registrations that differ, whose one the
    /// runtime takes isn't known; a key that can't be read, logged once, after which nothing is stamped: a copy goes in as it
    /// is, and the worst the runtime does with it is empty it (a cold start, as without a compile).</summary>
    static Delivery? Lookup(string exe, out RegistryRead read)
    {
        read = new(RegistryRead.None, []);
        if (!OperatingSystem.IsWindows()) return null;
        var matches = new List<Delivery>();
        var unread = false;
        T? Read<T>(string key, Func<T?> f) where T : class
        {
            try { return f(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                unread = true;
                if (Unreadable.TryAdd(key, 0)) Warn?.Invoke($"the shader delivery registration {key} can't be read: {e.Message}");
                return null;
            }
        }
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            var at = hive.Name + @"\Software\Microsoft\DirectX\ShaderCache";
            using var root = Read(at, () => hive.OpenSubKey(@"Software\Microsoft\DirectX\ShaderCache"));
            foreach (var installer in Read(at, () => root?.GetSubKeyNames()) ?? [])
            {
                if (installer.StartsWith(WarmInstaller, StringComparison.Ordinal)) continue;   // a compile's own, which a sweep may be removing
                var ip = at + "\\" + installer;
                using var ik = Read(ip, () => root!.OpenSubKey(installer));
                foreach (var app in Read(ip, () => ik?.GetSubKeyNames()) ?? [])
                {
                    var ap = ip + "\\" + app;
                    using var ak = Read(ap, () => ik!.OpenSubKey(app));
                    if (ak == null || Read(ap, () => ak.GetValue("ExePath")) is not string path || !SameExe(path, exe)) continue;
                    var (psdbs, families) = (new List<string>(), new List<string>());
                    using var comps = Read(ap, () => ak.OpenSubKey("Components"));
                    foreach (var c in Read(ap, () => comps?.GetSubKeyNames()) ?? [])
                    {
                        using var fams = Read(ap, () => comps!.OpenSubKey(c + @"\AdapterFamilies"));
                        foreach (var f in Read(ap, () => fams?.GetSubKeyNames()) ?? [])
                        {
                            using var fk = Read(ap, () => fams!.OpenSubKey(f));
                            if (Read(ap, () => fk?.GetValue("PSDBPath")) is string { Length: > 0 } p) { psdbs.Add(p); families.Add(f); }
                        }
                    }
                    if (Read(ap, () => (object)new Delivery(ak.GetValue("Name") as string ?? "", ak.GetValue("EngineName") as string ?? "",
                        Num(ak.GetValue("Version")), Num(ak.GetValue("EngineVersion")), psdbs, families)) is Delivery found) matches.Add(found);
                }
            }
        }
        AppIdentity[] ids = [.. matches.Select(m => m.Identity).Distinct()];
        var doubt = ids.Length > 1;
        // measured: with a key it can't read in the hive the runtime writes no identity either, so nothing is stamped
        read = new(unread ? RegistryRead.Unreadable : doubt ? RegistryRead.Differ : ids.Length == 1 ? RegistryRead.One : RegistryRead.None, ids);
        return doubt || unread || matches.Count == 0 ? null : matches[0] with
        {
            Psdbs = [.. PsdbsOf(matches).Select(p => p.Psdb)], Families = [.. PsdbsOf(matches).Select(p => p.Family)],
        };
    }

    /// <summary>The registrations' delivered dbs, each once, with the adapter family it was registered for.</summary>
    static IEnumerable<(string Psdb, string Family)> PsdbsOf(List<Delivery> matches) =>
        matches.SelectMany(m => m.Psdbs.Zip(m.Families ?? [])).Select(p => (Psdb: p.First, Family: p.Second)).DistinctBy(p => p.Psdb, StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the line naming a registration key that can't be read, once per key and session.</summary>
    public static Action<string>? Warn;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Unreadable = new(StringComparer.OrdinalIgnoreCase);

    static long Num(object? o) => o switch { long l => l, int i => (uint)i, _ => 0 };   // REG_DWORD is unsigned

    // measured: the runtime compares the registered path as written with the launched exe's final path (junctions followed),
    // so a registration at the real path holds through a junction, one at a junction path never does
    static bool SameExe(string registered, string exe) =>
        Games.GameFiles.Identity(exe)?.Final is { } final && final.Equals(registered, StringComparison.OrdinalIgnoreCase);

    // measured on NVIDIA 621: the runtime writes the registration's values unchanged and keeps a db stamped with them
    static bool Stamp(nint db, AppIdentity a) => Exec(db, $"update app_id set {a.Set}");

    /// <summary>The db or its -wal or -shm is a link: SQLite would follow it out of the folder.</summary>
    static bool LinkedDb(string file) => new[] { file, file + "-wal", file + "-shm" }.Any(f => File.Exists(f) && new FileInfo(f).Attributes.HasFlag(FileAttributes.ReparsePoint));

    // SCSKILLER_TEST_FOLD_PAUSE_MS: a pause before the merge locks the game's db and before a renamed staged db moves (E2E),
    // at most 5 s so a stray value can't hold a fold
    static void TestPause()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("SCSKILLER_TEST_FOLD_PAUSE_MS"), out var ms) && ms > 0) Thread.Sleep(Math.Min(ms, 5000));
    }

    /// <summary>Another handle on the file (the game's runtime, a warm): no exclusive open.</summary>
    static bool Held(string file)
    {
        if (!File.Exists(file)) return false;
        try { using var _ = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    static bool Open(string file, out nint db, bool uri = false)
    {
        if (sqlite3_open_v2(file, out db, 0x02 /* READWRITE */ | (uri ? 0x40 /* URI */ : 0), 0) == 0) return true;
        sqlite3_close_v2(db);
        return false;
    }

    /// <summary>The db as a merge's check of it may stand for: its <see cref="KeyFiles.Stamp"/> (length, write and change
    /// times, file id, first and last pages). Null with a WAL beside it, whose frames the stamp can't see.</summary>
    internal static string? CheckStamp(string db)
    {
        try { return File.Exists(db + "-wal") && new FileInfo(db + "-wal").Length > 0 ? null : KeyFiles.Stamp(db, strict: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }   // a page under SQLite's lock bytes
    }

    static string Immutable(string file) =>
        "file:" + file.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23").Replace("?", "%3f") + "?immutable=1";

    static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    static bool Exec(nint db, string sql) => Rows(db, sql, _ => { });

    static string? Text(nint db, string sql)
    {
        string? text = null;
        return Rows(db, sql, st => text = Marshal.PtrToStringUTF8(sqlite3_column_text(st, 0)) ?? "") ? text : null;
    }

    /// <summary>Runs <paramref name="sql"/> on the SQLite db <paramref name="file"/>, read only and as it is on disk (immutable); false
    /// when it isn't a readable db or the query fails.</summary>
    internal static bool Query(string file, string sql, Action<nint> row)
    {
        var uri = "file:" + file.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23").Replace("?", "%3f") + "?immutable=1";
        var rc = sqlite3_open_v2(uri, out var db, 0x01 /* READONLY */ | 0x40 /* URI */, 0);
        try { return rc == 0 && Rows(db, sql, row); }
        finally { sqlite3_close_v2(db); }
    }

    static bool Rows(nint db, string sql, Action<nint> row) => Step(db, sql, row) == Done;

    const int Done = 101;

    /// <summary>SQLite's result code of <paramref name="sql"/>: <see cref="Done"/> once every row went to <paramref name="row"/>.</summary>
    static int Step(nint db, string sql, Action<nint> row)
    {
        var rc = sqlite3_prepare_v2(db, sql, -1, out var st, 0);
        if (rc != 0) return rc;
        try
        {
            while ((rc = sqlite3_step(st)) == 100 /* ROW */) row(st);
            return rc;
        }
        finally { sqlite3_finalize(st); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(nint process, out IoCounters counters);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport(Sqlite)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out nint db, int flags, nint vfs);
    [DllImport(Sqlite)] static extern int sqlite3_close_v2(nint db);
    [DllImport(Sqlite)] static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint stmt, nint tail);
    [DllImport(Sqlite)] static extern int sqlite3_step(nint stmt);
    [DllImport(Sqlite)] internal static extern nint sqlite3_column_text(nint stmt, int col);
    [DllImport(Sqlite)] internal static extern long sqlite3_column_int64(nint stmt, int col);
    [DllImport(Sqlite)] static extern int sqlite3_finalize(nint stmt);
    [DllImport(Sqlite)] static extern nint sqlite3_errstr(int rc);
    [DllImport(Sqlite)] static extern void sqlite3_progress_handler(nint db, int ops, Progress? check, nint arg);
}

/// <summary>Unreal's user pipeline cache, <c>&lt;Saved&gt;\&lt;Project&gt;_&lt;ShaderPlatform&gt;.upipelinecache</c>. Saved's place varies
/// by game (e.g. <c>%LOCALAPPDATA%\Iris\Saved_Steam_&lt;id&gt;</c>), so it's found by file name in any Saved / Saved_* folder one
/// level under the roots; never inside the install (the shipped caches).</summary>
public static class UnrealUserCache
{
    /// <summary>The folder holding <c>Binaries\&lt;platform&gt;\</c> the exe is in; null when not laid out like that.</summary>
    public static string? Project(string exePath)
    {
        var bin = Path.GetDirectoryName(Path.GetDirectoryName(exePath));
        return bin != null && Path.GetFileName(bin).Equals("Binaries", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(Path.GetDirectoryName(bin)) is { Length: > 0 } project ? project : null;
    }

    public static IReadOnlyList<string> FilesOf(Game g, IEnumerable<string> roots)
    {
        if (Project(g.ExePath) is not { } project) return [];
        var name = new Regex($@"^{Regex.Escape(project)}_(PCD3D|SF_VULKAN|VULKAN)_[A-Z0-9_]+(\.stable)?\.upipelinecache$", RegexOptions.IgnoreCase);
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir)) + Path.DirectorySeparatorChar;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var files = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var app in Directory.EnumerateDirectories(root, "*", options))
                try
                {
                    foreach (var saved in Directory.EnumerateDirectories(app, "Saved*", options))
                        if (Path.GetFileName(saved) is var s && (s.Equals("Saved", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Saved_", StringComparison.OrdinalIgnoreCase)))
                            files.AddRange(Directory.EnumerateFiles(saved, "*.upipelinecache", options)
                                .Where(f => name.IsMatch(Path.GetFileName(f)) && !Path.GetFullPath(f).StartsWith(install, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return files;
    }

    /// <summary>The folder names a game's own caches go under in %LOCALAPPDATA%, My Games or ProgramData: its name, its
    /// install folder's, its Unreal project's (Tiny Tina's Wonderlands: <c>%LOCALAPPDATA%\Tiny Tina's Wonderlands\Saved</c>).</summary>
    public static IEnumerable<string> Folders(Game g) =>
        new[] { g.Name, Path.GetFileName(Path.TrimEndingDirectorySeparator(g.InstallDir)), Project(g.ExePath) }
            .OfType<string>().Select(n => n.Trim()).Where(n => n.Length > 0 && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <c>*.ushaderprecache</c> files (a shader and pipeline cache some Unreal games write themselves, and rebuild
    /// at their next start), or those of <paramref name="pattern"/>, in <c>&lt;root&gt;\&lt;folder&gt;</c> and two levels below
    /// (its Saved folder); never inside the install.</summary>
    /// <param name="noLinks">no folder or file that is a link (junction, symlink): what is read stays the game's own</param>
    public static IReadOnlyList<string> PrecacheFilesOf(Game g, IEnumerable<string> roots, IEnumerable<string> folders, string pattern = "*.ushaderprecache", bool noLinks = false)
    {
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir)) + Path.DirectorySeparatorChar;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = noLinks ? FileAttributes.ReparsePoint : 0, RecurseSubdirectories = true, MaxRecursionDepth = 2 };
        var names = folders.ToList();
        return roots.Where(Directory.Exists).SelectMany(r => names.Select(n => Path.Combine(r, n))).Where(Directory.Exists)
            .Where(d => !noLinks || !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
            .SelectMany(d => Directory.EnumerateFiles(d, pattern, options))
            .Where(f => !Path.GetFullPath(f).StartsWith(install, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Pipeline caches a game writes and rebuilds when they're gone, never a file it ships, each tied to its game's engine:
///   - Dawn (Guardians of the Galaxy): <c>bin\pipelineLibrary.cache</c> beside the shipped <c>bin\raw*.store2</c>
///     (<see cref="Carved.DawnStores"/>), a serialized ID3D12PipelineLibrary, which only loads on the GPU and driver that
///     wrote it;
///   - RE Engine: <c>shader.cache2</c> and <c>piplinelist.bin</c> beside the exe, written at the first start after an
///     install (RE Engine);
///   - GTA V Enhanced (RAGE, <c>GTA5_Enhanced.exe</c>): <c>*.d3d12PipelineCacheWindows</c> in
///     <c>Documents\Rockstar Games\GTAV Enhanced\Settings</c>, a pipeline library;
///   - HELLDIVERS 2 (Stingray, <c>helldivers2.exe</c>): <c>%APPDATA%\Arrowhead\Helldivers2\shader_cache\pipeline_library.cache</c>,
///     a pipeline library.
/// Folders outside the install are another game's for every other game of the engine, so those go by the exe too. A list of
/// the pipelines play created (War Thunder's dx12.cache, GTA V's warmup cache, Unreal's .upipelinecache) is never one:
/// only playing again refills it.</summary>
public static class OwnPipelineCaches
{
    static readonly string[] DawnFiles = ["rawshader.store2", "rawroot.store2", "rawpso.store2"], ReFiles = ["shader.cache2", "piplinelist.bin"];

    public static IReadOnlyList<string> FilesOf(Game g, EngineInfo? engine, string documents, string roamingAppData)
    {
        var bin = Games.GameFiles.Find(g.InstallDir, "bin");
        var files = new List<string>();
        if (DawnFiles.All(f => File.Exists(Games.GameFiles.Find(bin, f)))) files.Add(Games.GameFiles.Find(bin, "pipelineLibrary.cache"));
        if (engine?.Family == ReEngine.ReEngineReader.Family) files.AddRange(ReFiles.Select(f => Games.GameFiles.Find(Path.GetDirectoryName(g.ExePath)!, f)));
        var exe = Path.GetFileName(g.ExePath);
        if (engine?.Family == Rage.RageReader.Family && exe.Equals("GTA5_Enhanced.exe", StringComparison.OrdinalIgnoreCase)
            && Path.Combine(documents, "Rockstar Games", "GTAV Enhanced", "Settings") is var settings && Directory.Exists(settings))
            files.AddRange(Directory.EnumerateFiles(settings, "*.d3d12PipelineCacheWindows", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }));
        if (engine is { Family: Nixxes.NixxesReader.Family, Fork: Nixxes.NixxesReader.Stingray } && exe.Equals("helldivers2.exe", StringComparison.OrdinalIgnoreCase))
            files.Add(Path.Combine(roamingAppData, "Arrowhead", "Helldivers2", "shader_cache", "pipeline_library.cache"));
        return files.Where(File.Exists).ToList();
    }
}
