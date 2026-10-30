using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Vendors;

/// <summary>A Steam game's driver cache folder in Steam's shadercache (<paramref name="leaf"/>: Mesa's <see cref="Mesa"/> or
/// NVIDIA's <see cref="Nvidia"/>), and vkd3d-proton's cache files where the game runs, set aside for its before run so the
/// run is cold: each renamed beside itself (one file system, never a copy) once a journal of it and of every file in it is
/// on disk, and put back after the run only while each file is still the one set aside, byte for byte, and only where
/// nothing is. What the run made at its place is moved into <paramref name="keepDir"/> first (beside it when that is on
/// another file system), never deleted: Steam's pre-cache and its merge may write there too.</summary>
public sealed class SteamSetAside(string journalPath, string keepDir, string leaf) : IBeforeRunAside
{
    public const string Mesa = "mesa_shader_cache_sf", Nvidia = "nvidiav1";

    /// <summary>A folder or file set aside, by its path below the folder (a file: its name): its inode, change time and, for a
    /// file, its length and SHA-256.</summary>
    public sealed record Entry(string Path, ulong Inode, long Changed, long Length, string? Hash);

    /// <summary>A folder, or a file (<paramref name="IsFile"/>), renamed from <paramref name="From"/> to <paramref name="To"/>:
    /// the folders and files below it, or the file alone. <paramref name="Absent"/>: nothing was there, what the run makes
    /// there still moves away.</summary>
    public sealed record Item(string From, string To, List<Entry> Entries, bool IsFile = false, bool Absent = false);

    /// <summary>What is set aside for <paramref name="GameId"/>'s run: the cache folder, then vkd3d-proton's files. A journal
    /// that names one folder in <paramref name="From"/>, <paramref name="To"/> and <paramref name="Entries"/> reads as that folder.</summary>
    public sealed record Journal(string GameId, List<Item>? Items = null, string? From = null, string? To = null, List<Entry>? Entries = null);

    public const string Suffix = ".scskiller-set-aside", KeptSuffix = ".scskiller-before-run-";
    const uint ModeRegular = 0x8000;
    const int EXDev = 18, EInval = 22, ENoSys = 38;

    public string Leaf => leaf;

    /// <summary>renameat2's RENAME_NOREPLACE, its errno. Tests: a stand-in.</summary>
    internal Func<string, string, int> Rename { get; init; } = Libc.RenameNew;

    /// <summary>Whether a process other than this one has one of the files open: null when one might. Tests: a stand-in.</summary>
    public Func<IReadOnlyList<string>, bool?> Held { get; init; } = paths => AppCacheFiles.ProcessesUsing(paths) is var (holding, unknown)
        && holding.Any(p => p != (nuint)Environment.ProcessId) ? true : unknown ? null : false;

    /// <summary>Whether the folder the run made at the cache folder's place may move away, beyond no process holding a file
    /// of it; null: it may.</summary>
    public Func<string, bool>? Settled { get; init; }

    public Action<string>? Log { get; init; }

    public Journal? Pending
    {
        get
        {
            try { return File.Exists(journalPath) ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath)) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }

    public string? PendingGame => Pending?.GameId;

    public string Folder => Pending is { } j ? Items(j).FirstOrDefault(i => !i.Absent)?.To ?? j.Items?.FirstOrDefault()?.From ?? "" : "";

    public bool AfterRunReads => true;

    /// <summary>Sets <paramref name="folder"/> and <paramref name="files"/> aside for <paramref name="gameId"/>'s run, and
    /// journals those that aren't there, so what the run makes there moves away too. InvalidOperationException with why,
    /// nothing moved, when a journal is there (it may track what was set aside before), one of them or the folder holding it
    /// is a link, anything in the folder is neither a folder nor a file, a process has a file of them open, or a rename fails
    /// (the journal stays then, for Restore to put back what moved).</summary>
    public void SetAside(string gameId, string folder, IReadOnlyList<string>? files = null)
    {
        using var own = Lock() ?? throw new InvalidOperationException("another SCSKiller is setting the cache aside or putting it back, try again in a moment");
        if (File.Exists(journalPath)) throw new InvalidOperationException($"a cache is set aside already, see {journalPath}");
        List<Item> items = [There(folder, Kind.Folder)
            ? new(folder, folder + Suffix, Entries(folder, hash: true) ?? throw new InvalidOperationException($"{folder} holds a link or a file that can't be read"))
            : new(folder, folder + Suffix, [], Absent: true)];
        foreach (var f in files ?? [])
            items.Add(There(f, Kind.File) ? new(f, f + Suffix, [Of(f, Path.GetFileName(f), hash: true) ?? throw new InvalidOperationException($"{f} can't be read")], IsFile: true)
                : new(f, f + Suffix, [], IsFile: true, Absent: true));
        var moved = items.Where(i => !i.Absent).ToList();
        if (moved.FirstOrDefault(i => !i.IsFile) is { } dir && Held([.. dir.Entries.Where(e => e.Hash != null).Select(e => Path.Combine(folder, e.Path))]) == true)
            throw new InvalidOperationException($"a process has a file of {folder} open");
        if (moved.Where(i => i.IsFile).Select(i => i.From).ToList() is { Count: > 0 } held && Held(held) == true)
            throw new InvalidOperationException($"a process has {string.Join(" or ", held)} open");
        if (moved.FirstOrDefault(i => KindOf(i.To) != Kind.Missing) is { } there) throw new InvalidOperationException($"{there.To} is there already");
        Write(new(gameId, items));   // before the moves
        foreach (var i in moved)
        {
            // no delete on a failed move: the journal may be another instance's that won it
            if (Rename(i.From, i.To) is var e and not 0) throw new InvalidOperationException($"{i.From} couldn't be renamed: {Why(e)}");
            Libc.SyncFolder(Path.GetDirectoryName(i.From)!);
        }
    }

    /// <summary>Whether <paramref name="path"/> holds a <paramref name="kind"/> to set aside; InvalidOperationException when
    /// it, or the folder holding it, is a link or anything else.</summary>
    static bool There(string path, Kind kind)
    {
        var parent = Path.GetDirectoryName(path)!;
        if (KindOf(parent) is Kind.File or Kind.Other) throw new InvalidOperationException($"{parent} is a link or can't be read");
        var now = KindOf(path);
        if (now == kind) return true;
        if (now == Kind.Missing) return false;
        throw new InvalidOperationException($"{path} is a link or can't be read");
    }

    /// <summary>Puts each item back once what the run made at its place is moved away (KeepMade). That waits while a process
    /// has a file of it open or, for the cache folder, until <see cref="Settled"/>: nothing is returned for it then and the
    /// journal keeps it for the next try. An item whose set-aside copy is gone is dropped and nothing of it is touched, and a
    /// journal that names any other folder or file is dropped whole. The files that couldn't be put back: the set-aside copy
    /// changed, the rename failed (something came back at its place meanwhile), or a file couldn't be read or moved, or what
    /// the run made where nothing was, when it can't move.</summary>
    public List<string> Restore(string? old = null)
    {
        if (!File.Exists(journalPath)) return [];
        // another SCSKiller's set-aside or put-back: the next poll tries again
        using var own = Lock();
        if (own == null || Pending is not { } j) return [];
        var items = Items(j);
        var left = new List<string>();
        try
        {
            if (items.FirstOrDefault(i => !Ours(i)) is { } other)
            {
                Log?.Invoke($"{journalPath} names {other.From}, not a cache set aside here: the journal is dropped, nothing moved");
                File.Delete(journalPath);
                return [];
            }
            var stay = new List<Item>();
            var app = items.FirstOrDefault(i => !i.IsFile) is { } f ? Path.GetFileName(Path.GetDirectoryName(f.From))! : "run";
            foreach (var i in items)
            {
                bool? back;
                try { back = Restore(i, app); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log?.Invoke($"{i.To} wasn't put back: {e.Message}");
                    back = false;
                }
                if (back == true) continue;
                stay.Add(i);
                if (back == false)
                {
                    List<string> files = i.Absent ? [i.From] : i.IsFile ? [i.To] : [.. i.Entries.Where(e => e.Hash != null).Select(e => Path.Combine(i.To, e.Path))];
                    left.AddRange(files.Count > 0 ? files : [i.To]);   // a tree of folders alone fails all the same
                }
            }
            if (stay.Count == 0) File.Delete(journalPath);
            else if (stay.Count < items.Count) Write(new(j.GameId, stay));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Invoke($"{journalPath} stays as it was: {e.Message}"); }
        return left;
    }

    /// <summary>One item: true once it's back, or its set-aside copy is gone (nothing touched); null while what the run made
    /// at its place waits; false when it can't be put back.</summary>
    bool? Restore(Item i, string app)
    {
        var kind = i.IsFile ? Kind.File : Kind.Folder;
        if (!i.Absent)
            switch (KindOf(i.To))
            {
                case Kind.Missing: return true;
                case var k when k != kind || !Same(i, hash: false): return false;
            }
        // nothing moves through a folder swapped for a link since
        switch (KindOf(Path.GetDirectoryName(i.From)!))
        {
            case Kind.Missing when i.Absent: return true;
            case not Kind.Folder: return false;
        }
        if (!i.Absent)
        {
            if (Held(i.IsFile ? [i.To] : [.. i.Entries.Where(e => e.Hash != null).Select(e => Path.Combine(i.To, e.Path))]) == true) return null;
            // before what the run made moves: a copy that can't go back leaves it in place
            if (!Same(i, hash: true)) return false;
        }
        switch (KindOf(i.From))
        {
            case Kind.Missing: break;
            case var k when k != kind: return false;
            default:
                List<string> made = i.IsFile ? [i.From] : [.. Directory.EnumerateFiles(i.From, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })];
                if (Held(made) == true || !i.IsFile && Settled?.Invoke(i.From) == false) return null;
                if (!KeepMade(i, app)) return false;
                break;
        }
        if (i.Absent) return true;
        if (Rename(i.To, i.From) != 0) return false;
        Libc.SyncFolder(Path.GetDirectoryName(i.From)!);
        return true;
    }

    /// <summary>Moves what the run made at the item's place into keepDir, named after the app's folder (and a file's name).</summary>
    bool KeepMade(Item i, string app)
    {
        var stamp = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}";
        var name = i.IsFile ? $"{app}-{stamp}-{Path.GetFileName(i.From)}" : $"{app}-{stamp}";
        Directory.CreateDirectory(keepDir);
        var to = Path.Combine(keepDir, name);
        var e = Rename(i.From, to);
        if (e == EXDev) e = Rename(i.From, to = i.From + KeptSuffix + (i.IsFile ? stamp : name));
        Log?.Invoke(e == 0 ? $"what the before run made at {i.From} was moved to {to}" : $"{i.From} couldn't be moved to {to}: {Why(e)}");
        if (e != 0) return false;
        Libc.SyncFolder(Path.GetDirectoryName(to)!);
        Libc.SyncFolder(Path.GetDirectoryName(i.From)!);
        return true;
    }

    /// <summary>The journal's lock across a whole SetAside or Restore, also between processes (flock); null while another
    /// holds it.</summary>
    FileStream? Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        try { return new FileStream(journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when (IoErrors.HeldElsewhere(e)) { return null; }
    }

    static string Why(int errno) => errno is EInval or ENoSys ? "the file system can't rename without replacing" : Marshal.GetPInvokeErrorMessage(errno);

    static List<Item> Items(Journal j) => j.Items ?? (j is { From: { } from, To: { } to, Entries: { } entries } ? [new(from, to, entries)] : []);

    /// <summary>A Steam cache folder of this leaf in an app's folder of shadercache, or vkd3d-proton's cache file, renamed beside itself.</summary>
    bool Ours(Item i) =>
        Path.IsPathFullyQualified(i.From) && i.To == i.From + Suffix && Path.GetFullPath(i.From) == i.From
        && (i.IsFile ? i.Entries.Count == (i.Absent ? 0 : 1) && Path.GetFileName(i.From) is SteamCachePlace.Vkd3dCache or SteamCachePlace.Vkd3dWrite
            : Path.GetFileName(i.From) == leaf && Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(i.From))) == "shadercache");

    static readonly Dictionary<string, bool> Hashed = [];

    /// <summary>The set-aside copy holds the item's entries alone, each with its identity (a renamed file's own change time
    /// moves with the rename); with <paramref name="hash"/> each file's bytes too, read once for an unchanged identity.</summary>
    bool Same(Item i, bool hash)
    {
        var now = i.IsFile ? Of(i.To, i.Entries[0].Path, hash: false) is { } f ? new List<Entry> { f } : null : Entries(i.To, hash: false);
        if (now == null || now.Count != i.Entries.Count) return false;
        var was = i.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        if (!now.All(n => was.TryGetValue(n.Path, out var w) && w.Inode == n.Inode && (i.IsFile || w.Changed == n.Changed) && w.Length == n.Length
                && (w.Hash == null) == (n.Hash == null))) return false;
        if (!hash) return true;
        var key = journalPath + "\n" + i.To + "\n" + string.Join('\n', now.Select(n => $"{n.Path}:{n.Inode}:{n.Changed}:{n.Length}"));
        lock (Hashed)
            if (Hashed.TryGetValue(key, out var same)) return same;
        var bytes = i.Entries.All(e => e.Hash == null || Sha(i.IsFile ? i.To : Path.Combine(i.To, e.Path)) == e.Hash);
        lock (Hashed) Hashed[key] = bytes;
        return bytes;
    }

    enum Kind { Missing, Folder, File, Other }

    /// <summary>What the name holds, a link not followed: Other for a link, anything but a folder or a file, or a name that
    /// can't be read.</summary>
    static Kind KindOf(string path)
    {
        if (Libc.Lstat(path) is { } s) return (s.Mode & Libc.ModeType) switch { Libc.ModeDirectory => Kind.Folder, ModeRegular => Kind.File, _ => Kind.Other };
        try
        {
            File.GetAttributes(path);
            return Kind.Other;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return Kind.Missing; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Kind.Other; }
    }

    /// <summary>Every folder and file below <paramref name="root"/>, no link followed; null when one is anything else or
    /// can't be read.</summary>
    static List<Entry>? Entries(string root, bool hash)
    {
        var list = new List<Entry>();
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                if (Of(path, Path.GetRelativePath(root, path), hash) is { } e) list.Add(e);
                else return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return list;
    }

    /// <summary>The folder or file at <paramref name="path"/> as the entry <paramref name="name"/>; null for anything else.</summary>
    static Entry? Of(string path, string name, bool hash)
    {
        if (Libc.Lstat(path) is not { } st || Libc.StatNoFollow(path) is not { } id) return null;
        var type = st.Mode & Libc.ModeType;
        if (type != Libc.ModeDirectory && type != ModeRegular) return null;
        var file = type == ModeRegular;
        if (file && Libc.Links(path) is not { Links: 1 }) return null;   // a hard link's other name could change it unseen
        return new(name, id.Inode, id.ChangedSeconds * 1_000_000_000 + id.ChangedNanoseconds,
            file ? new FileInfo(path).Length : -1, file ? hash ? Sha(path) : "" : null);
    }

    static string Sha(string path)
    {
        using var f = new FileStream(Libc.OpenNoFollow(path), FileAccess.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    void Write(Journal j)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var tmp = journalPath + ".tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(f, j);
            f.Flush(flushToDisk: true);   // on disk before the move: a power cut after it still finds the journal
        }
        File.Move(tmp, journalPath, overwrite: true);
        Libc.SyncFolder(Path.GetDirectoryName(journalPath)!);   // the journal's name too, before the folder moves
    }
}
