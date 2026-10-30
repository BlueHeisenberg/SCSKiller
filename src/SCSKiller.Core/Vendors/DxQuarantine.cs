using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SCSKiller.Core.Vendors;

/// <summary>Driver cache set aside for one game's before run under a journal written before the first move, put back once
/// the run is over or, after a crash, at the next start.</summary>
public interface IBeforeRunAside
{
    /// <summary>The game whose run has its cache set aside; null when nothing is.</summary>
    string? PendingGame { get; }

    /// <summary>Where it is while set aside.</summary>
    string Folder { get; }

    /// <summary>The game's own cache, which the after run reads: back before that run, and while its put-back waits the
    /// pair waits too.</summary>
    bool AfterRunReads { get; }

    /// <summary>Puts it back (<paramref name="old"/>: a key whose files are the game's old cache, not put back). The files
    /// that couldn't be; the journal keeps them for the next try.</summary>
    List<string> Restore(string? old = null);
}

/// <summary>Driver cache files set aside for a before run whose game's key isn't known, so no clear can find the game's old
/// cache among the others: AMD's DirectX 11 DxCache (the driver doesn't always hold that file open), or NVIDIA's DXCache
/// (<paramref name="nvidia"/>). Every file of a key not kept is renamed into a folder beside the cache (same volume: a
/// rename, never a copy) and, once the run is over, put back, except the files of a key learned as the game's, which are
/// its old cache. A journal of the moves is written before the first one, so a run cut off by a crash is put back at the
/// next start. With <paramref name="glCache"/>, a game's own NVIDIA Vulkan cache folder (Steam's GLCache folder of its app):
/// all of its &lt;driver&gt;\&lt;gpu&gt;\ files or none, each with its length and SHA-256, put back only when both match, and
/// what the run made there moved into the data folder (<see cref="KeepDir"/>), never deleted.</summary>
public sealed class DxQuarantine(string dxDir, string journalPath, bool nvidia = false, bool glCache = false) : IBeforeRunAside
{
    public sealed record Move(string From, string To, long Length = -1, string? Sha256 = null);
    public sealed record Journal(string GameId, List<Move> Moves, List<string> Skipped, string? Dir = null, bool Archived = false);

    public string Folder => Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dxDir))!, Path.GetFileName(Path.TrimEndingDirectorySeparator(dxDir)) + ".scskiller-quarantine");

    public string? PendingGame => Pending?.GameId;

    // the GLCache folder is the game's own cache, which the after run reads
    public bool AfterRunReads => glCache;

    /// <summary>glCache: the folder is in use without a file held open (Steam's replay), so the put-back waits.</summary>
    public Func<bool>? Busy { get; init; }

    public Journal? Pending => Read(journalPath);

    public static Journal? Read(string journalPath)
    {
        try { return File.Exists(journalPath) ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    Journal New(string gameId, List<Move> moves, List<string> skipped) => new(gameId, moves, skipped, glCache ? dxDir : null);

    string? KeyOf(string fileName) => !nvidia ? AmdAppCache.Key(fileName, d3d12: false)
        : NvidiaAppCache.Key(fileName) is { } k && fileName.AsSpan(4, 4).Equals("a91d", StringComparison.OrdinalIgnoreCase) ? k : null;

    /// <summary>The cache folder or the set-aside one is a link: its files may be anywhere, so none is moved or deleted.</summary>
    bool Linked() => new[] { dxDir, Folder }.Any(d => Directory.Exists(d) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
        || glCache && Directory.Exists(Folder) && !NoLinks(new DirectoryInfo(Folder));

    static bool NoLinks(DirectoryInfo d)
    {
        try { return d.EnumerateDirectories().All(s => (s.Attributes & FileAttributes.ReparsePoint) == 0 && NoLinks(s)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // NVIDIA's lengths step 16x, so equal lengths are common: the bytes its header says it holds tell more
    long Holds(string path) => (nvidia ? NvidiaAppCache.UsedBytes(path) : null) ?? new FileInfo(path).Length;

    /// <summary>Sets aside every cache file whose key isn't <paramref name="keep"/>'s, for <paramref name="gameId"/>'s run:
    /// the cache folder's own files only, no links. A key with a file a process holds open stays whole, and a file that
    /// doesn't rename stays: both listed as skipped, as is every file when a folder is a link or a journal that can't be
    /// read is there (it may track files set aside before: never overwritten). Returns the journal, written even with
    /// nothing moved: the run's end learns the key the game made from it.</summary>
    public Journal SetAside(string gameId, Func<string, bool> keep, bool held = false)
    {
        using var own = Lock();
        if (own == null) return New(gameId, [], [dxDir]);   // another SCSKiller sets aside or puts back: nothing moved
        return glCache ? SetAsideTree(gameId, held) : SetAsideKeys(gameId, keep);
    }

    /// <summary>The journal's lock across processes (the app and the command line on one data folder), held for a whole
    /// set-aside or put-back; null while another holds it.</summary>
    FileStream? Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
        try { return new FileStream(journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when (App.IoErrors.HeldElsewhere(e)) { return null; }
    }

    Journal SetAsideKeys(string gameId, Func<string, bool> keep)
    {
        var plan = new List<Move>();
        var skipped = new List<string>();
        var unread = File.Exists(journalPath);
        var blocked = unread || Linked();
        if (Directory.Exists(dxDir))
            foreach (var key in new DirectoryInfo(dxDir).EnumerateFiles(nvidia ? "*.nvph" : "*.parc")
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && KeyOf(f.Name) is { } k && !keep(k))
                .GroupBy(f => KeyOf(f.Name)!, StringComparer.OrdinalIgnoreCase))
                if (blocked || key.Any(f => AppCacheFiles.ProcessesUsing(f.FullName).Count > 0)) skipped.AddRange(key.Select(f => f.FullName));
                else plan.AddRange(key.Select(f => new Move(f.FullName, Path.Combine(Folder, f.Name))));
        if (plan.Count == 0)
        {
            var none = new Journal(gameId, [], skipped);
            if (!unread) Write(none, replace: false);
            return none;
        }
        Directory.CreateDirectory(Folder);
        Write(new(gameId, plan, skipped), replace: false);   // before the first move
        var moved = new List<Move>();
        foreach (var m in plan)
            if (Rename(m.From, m.To)) moved.Add(m);
            else skipped.Add(m.From);
        var j = new Journal(gameId, moved, skipped);
        Write(j);
        return j;
    }

    /// <summary>Puts the set-aside files back, except <paramref name="old"/>'s (the game's old cache), which are deleted. A
    /// file made again at its place meanwhile: the set-aside one waits while that one is in use (its header can't be read),
    /// then the one holding more stays, on a tie the one made meanwhile. A journal entry that isn't a cache file moved from
    /// the cache folder into the set-aside one is dropped untouched; with either folder a link, nothing is touched. Returns
    /// the files that couldn't be put back; the journal keeps them, and those waiting, for the next try.</summary>
    public List<string> Restore(string? old = null)
    {
        using var own = Lock();
        if (own == null) return [];   // another SCSKiller puts it back: what's left stays in the journal
        if (Pending is not { } j) return [];
        if (Linked()) return j.Moves.Select(m => m.To).ToList();
        if (glCache) return RestoreTree(j, Busy?.Invoke() == true);
        var left = new List<Move>();
        var waiting = new List<Move>();
        var kept = Path.Combine(KeepDir, $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(dxDir))}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}");
        foreach (var m in j.Moves.Where(Ours))
        {
            try
            {
                if (!Exists(m.To)) continue;
                // the game's old cache, as Clear cache deletes it
                if (old != null && string.Equals(KeyOf(Path.GetFileName(m.To)), old, StringComparison.OrdinalIgnoreCase)) { File.Delete(m.To); continue; }
                if (File.Exists(m.From))
                {
                    if (AppCacheFiles.ProcessesUsing(m.From).Count > 0) { waiting.Add(m); continue; }
                    // the one holding less is kept in the data folder, never deleted
                    if (Holds(m.From) >= Holds(m.To))
                    {
                        if (!Keep(m.To, Path.Combine(kept, Path.GetFileName(m.To)))) left.Add(m);
                        continue;
                    }
                    if (!Keep(m.From, Path.Combine(kept, Path.GetFileName(m.From))))
                    {
                        left.Add(m);
                        continue;
                    }
                }
                if (!Rename(m.To, m.From)) left.Add(m);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left.Add(m); }
        }
        if (left.Count + waiting.Count > 0) { Write(j with { Moves = [.. left, .. waiting] }); return left.Select(m => m.To).ToList(); }
        File.Delete(journalPath);
        try { Directory.Delete(Folder); }
        catch (IOException) { }   // not empty: something else put a file there
        return [];
    }

    /// <summary>File.Exists says no when the file can't be read too: only a file that is gone drops its entry.</summary>
    static bool Exists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    bool Ours(Move m)
    {
        try
        {
            var (from, to) = (Path.GetFullPath(m.From), Path.GetFullPath(m.To));
            if (glCache) return Below(dxDir, from) is { } rel && rel == Below(Folder, to);
            return KeyOf(Path.GetFileName(from)) != null && Path.GetFileName(from) == Path.GetFileName(to)
                && string.Equals(Path.GetDirectoryName(from), Path.GetFullPath(Path.TrimEndingDirectorySeparator(dxDir)), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(to), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Under <see cref="Lock"/>. <paramref name="replace"/> false: a set-aside's first journal, which never takes the
    /// place of one already there (IOException, nothing moved).</summary>
    void Write(Journal j, bool replace = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var tmp = journalPath + ".tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(f, j);
            f.Flush(flushToDisk: true);   // on disk before the first move: a power cut after it still finds the journal
        }
        File.Move(tmp, journalPath, overwrite: replace);
    }

    /// <summary>glCache: every file is the game's, so all move or none. A file held open, <paramref name="held"/> (the cache in
    /// use otherwise: Steam's replay), a link or anything but &lt;driver&gt;\&lt;gpu&gt;\ files keeps them all in place, and a
    /// rename that fails puts back the ones moved before it.</summary>
    Journal SetAsideTree(string gameId, bool held)
    {
        var unread = File.Exists(journalPath);
        var files = Layout();
        var plan = new List<Move>();
        var blocked = unread || held || files == null || Linked();
        var hold = new List<FileStream>();
        try
        {
            foreach (var f in blocked ? [] : files!)
                if (Open(f.FullName, hold) is { } fp) plan.Add(new(f.FullName, Path.Combine(Folder, Path.GetRelativePath(dxDir, f.FullName)), fp.Length, fp.Sha256));
                else
                {
                    blocked = true;
                    break;
                }
            if (blocked || plan.Count == 0)
            {
                var none = New(gameId, [], !blocked ? [] : files is { Count: > 0 } ? [.. files.Select(f => f.FullName)] : [dxDir]);
                if (!unread) Write(none, replace: false);
                return none;
            }
            Write(New(gameId, plan, []), replace: false);   // before the first move
            var moved = new List<Move>();
            foreach (var m in plan)
            {
                if (Put(m.From, m.To))
                {
                    moved.Add(m);
                    continue;
                }
                var undone = New(gameId, [.. moved.Where(x => !Put(x.To, x.From))], [.. plan.Select(p => p.From)]);
                Write(undone);
                return undone;
            }
            return New(gameId, moved, []);
        }
        finally { foreach (var h in hold) h.Dispose(); }
    }

    /// <summary>glCache: what the run made in the cache folder goes into <see cref="KeepDir"/>, then each set-aside file whose
    /// length and SHA-256 are the journal's goes back. An entry whose set-aside file is gone
    /// was never moved (a set-aside cut off midway), or went back on an earlier try: its file is the original and stays.
    /// Waits, nothing moved and the journal kept, while any of the files is held open, <paramref name="held"/>, or Steam may
    /// still merge the run's files (<see cref="MergeWait"/>). The
    /// journal keeps every entry until all are back, and once the run's files are archived (Archived) nothing in the cache
    /// folder is taken for the run's again. Returns the files left set aside, as Restore.</summary>
    List<string> RestoreTree(Journal j, bool held)
    {
        var moves = j.Moves.Where(Ours).ToList();
        var aside = moves.Where(m => Exists(m.To)).ToList();
        var unmoved = moves.Except(aside).Select(m => Path.GetFullPath(m.From)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // skipped: nothing stayed set aside (or a rename back failed), so the run's files and the originals can't be told apart
        List<FileInfo>? made = j.Skipped.Count > 0 || j.Archived ? [] : Layout()?.Where(f => !unmoved.Contains(f.FullName)).ToList();
        if (made == null) return [.. aside.Select(m => m.To)];
        // Steam folds the game's steamapp_shader_cache files into its merged ones after the exit, then deletes them
        if (made.Any(f => f.Name.StartsWith("steamapp_shader_cache", StringComparison.OrdinalIgnoreCase)) && made.Max(f => f.LastWriteTimeUtc) > DateTime.UtcNow - MergeWait) return [];
        var hold = new List<FileStream>();
        try
        {
            if (held || made.Any(f => Open(f.FullName, hold, hash: false) == null)) return [];
            var proved = aside.Select(m => (Move: m, Has: Open(m.To, hold))).ToList();
            if (proved.Any(p => p.Has == null)) return [];
            // a set-aside file that isn't the one journaled: nothing moves, so the place keeps what it has
            if (proved.Where(p => p.Has!.Value.Length != p.Move.Length || p.Has.Value.Sha256 != p.Move.Sha256).Select(p => p.Move.To).ToList() is { Count: > 0 } changed)
                return changed;
            if (made.Count > 0)
            {
                // <data>\before-run-caches\<app>-<stamp> is the run's nvidiav1 folder, as on Linux
                var nv = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dxDir))!;
                var archive = Path.Combine(KeepDir, $"{Path.GetFileName(Path.GetDirectoryName(nv))}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}", Path.GetFileName(Path.TrimEndingDirectorySeparator(dxDir)));
                if (made.Select((f, i) => (f, i)).Any(x => !Keep(x.f.FullName, Path.Combine(archive, Path.GetRelativePath(dxDir, x.f.FullName)), hold[x.i])))
                    return [.. aside.Select(m => m.To)];
                Write(j with { Archived = true });
            }
            var left = proved.Where(p => p.Has!.Value.Length != p.Move.Length || p.Has.Value.Sha256 != p.Move.Sha256 || Exists(p.Move.From) || !Put(p.Move.To, p.Move.From))
                .Select(p => p.Move).ToList();
            if (left.Count > 0)
            {
                Write(j with { Archived = true });
                return [.. left.Select(m => m.To)];
            }
        }
        finally { foreach (var h in hold) h.Dispose(); }
        File.Delete(journalPath);
        foreach (var d in Directory.Exists(Folder) ? Directory.EnumerateDirectories(Folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(Folder) : [])
            try { Directory.Delete(d); }
            catch (IOException) { }   // not empty: something else put a file there
        return [];
    }

    /// <summary>The cache folder's files, each &lt;driver&gt;\&lt;gpu&gt;\&lt;file&gt; as NVIDIA lays them out. Null when anything
    /// else is there, a folder or file is a link, or it can't be read.</summary>
    List<FileInfo>? Layout()
    {
        var files = new List<FileInfo>();
        if (!Directory.Exists(dxDir)) return files;
        static bool Plain(FileSystemInfo i) => (i.Attributes & FileAttributes.ReparsePoint) == 0;
        try
        {
            foreach (var a in new DirectoryInfo(dxDir).EnumerateFileSystemInfos())
            {
                if (a is not DirectoryInfo da || !Plain(a)) return null;
                foreach (var b in da.EnumerateFileSystemInfos())
                {
                    if (b is not DirectoryInfo db || !Plain(b)) return null;
                    foreach (var c in db.EnumerateFileSystemInfos())
                        if (c is FileInfo f && Plain(f)) files.Add(f);
                        else return null;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return files;
    }

    /// <summary>The path below <paramref name="root"/> when it is &lt;driver&gt;\&lt;gpu&gt;\&lt;file&gt;; null for any other.</summary>
    static string? Below(string root, string full) =>
        Path.GetRelativePath(Path.GetFullPath(root), full) is var rel && !Path.IsPathRooted(rel)
        && rel.Split(Path.DirectorySeparatorChar) is { Length: 3 } parts && !parts.Any(p => p is "" or "." or "..") ? rel : null;

    /// <summary>Length and SHA-256 of a file no process holds, opened sharing only delete and kept in <paramref name="hold"/>:
    /// nothing else opens it until it is renamed and the handle closes. Null when it is held or can't be read.</summary>
    static (long Length, string? Sha256)? Open(string path, List<FileStream> hold, bool hash = true)
    {
        if (AppCacheFiles.ProcessesUsing(path).Count > 0) return null;
        try
        {
            var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
            hold.Add(f);
            return (f.Length, hash ? Convert.ToHexString(SHA256.HashData(f)) : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Where files a run made, or a file that lost to its namesake, are kept: before-run-caches beside the journal,
    /// in the data folder, as on Linux.</summary>
    string KeepDir => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(journalPath))!, "before-run-caches");

    // Steam merges at the game's exit: a run's file unchanged this long isn't being merged
    static readonly TimeSpan MergeWait = TimeSpan.FromMinutes(2);

    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/>: a rename on one volume, else a copy whose length and
    /// SHA-256 match the source's before the source goes. <paramref name="open"/>: the source's handle that keeps others out
    /// (read-only, sharing delete), read for the copy and closed once the source is gone. False with the source in place.</summary>
    internal static bool Keep(string from, string to, FileStream? open = null)
    {
        const int NotSameDevice = 17;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (Rename(from, to)) return true;
            // each try keeps into a folder of its own stamp, so a copy a crash left never blocks the next
            if (Marshal.GetLastPInvokeError() != NotSameDevice || File.Exists(to)) return false;
            var src = open ?? new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                var tmp = to + ".partial";
                src.Position = 0;
                string want, got;
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buf = new byte[1 << 20];
                        for (int n; (n = src.Read(buf)) > 0;) { sha.AppendData(buf, 0, n); dst.Write(buf, 0, n); }
                        want = Convert.ToHexString(sha.GetHashAndReset());
                    }
                    dst.Flush(flushToDisk: true);
                    dst.Position = 0;
                    got = dst.Length == src.Length ? Convert.ToHexString(SHA256.HashData(dst)) : "";
                }
                if (got != want)
                {
                    File.Delete(tmp);   // the copy only: the source stays
                    return false;
                }
                File.Move(tmp, to);
                File.Delete(from);
            }
            finally { src.Dispose(); }   // its name is free once the last handle closes
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static bool Put(string from, string to)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            return Rename(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A rename only: MoveFileEx without MOVEFILE_COPY_ALLOWED fails across volumes instead of copying.</summary>
    static bool Rename(string from, string to) => MoveFileExW(from, to, 0);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool MoveFileExW(string existing, string newName, uint flags);
}
