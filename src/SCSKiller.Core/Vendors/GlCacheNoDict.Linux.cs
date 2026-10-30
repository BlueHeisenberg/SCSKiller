using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SCSKiller.Core.App;
using ZstdSharp;
using ZstdSharp.Unsafe;
using Record = SCSKiller.Core.Vendors.GlCacheFile.Record;

namespace SCSKiller.Core.Vendors;

/// <summary>Rewrites the warm's NVIDIA cache files (<see cref="Wine.CacheEnvironment"/>'s app name <paramref name="appName"/>,
/// in the game's proton-cache folder <paramref name="gameDir"/>) so no entry needs a zstd dictionary (<see cref="GlCacheFile"/>).
/// A process decodes with one dictionary, its own file's, else the first read-only file's that has one, and entries under
/// any other miss (measured: a warm in two files left 10,805 of 11,312 pipelines to compile in game, 95 once rewritten).
/// Each entry under the dictionary is decoded and stored as plain zstd as the driver stores its first entries (level 1,
/// content size, no checksum, no dictionary id), entries already plain keep their bytes, and the dictionary entry goes.
/// A set goes to free file numbers the game reads, what would pass <see cref="FileLimit"/> to the next one, in key order. The
/// game's own files are never touched.
/// The new sets are written to temp files under proton-cache/nodict (outside the folder whose size the driver caps), read
/// back and checked against the original, listed with their lengths in a commit file, and given their free names (never
/// replacing a file), each .bin before its .toc. Only then is the original deleted, its .toc first. So each .toc on disk
/// always has its whole .bin, the new and the old set holding the same keys meanwhile. <see cref="Recover"/> settles a
/// rewrite cut short before any warm process writes again (<see cref="Settle"/>), and drops temp files no commit lists.</summary>
sealed class GlCacheNoDict(string gameDir, string appName)
{
    /// <summary>The bytes a .bin may hold: its entries' offsets are u32. Tests: a smaller one.</summary>
    internal long FileLimit { get; init; } = 1L << 32;

    /// <summary>The most the game's NVIDIA folder may hold after a rewrite.</summary>
    internal long Budget { get; init; } = NvidiaLinux.WarmBudget;

    /// <summary>The most it may hold while a set and its rewrite are both there: past it the driver deletes whole files.</summary>
    internal long Cap { get; init; } = NvidiaLinux.CacheSize;

    /// <summary>The warm's file numbers the game reads (<see cref="Wine.WarmFiles"/>): where entries past
    /// <see cref="FileLimit"/> may go.</summary>
    internal int Files { get; init; } = Wine.WarmFiles;

    internal int Threads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 16);

    /// <summary>Whether a process other than this one has the file open. Tests: a stand-in.</summary>
    internal Func<string, bool> Held { get; init; } = path => AppCacheFiles.ProcessesUsing(path).Any(p => p != (nuint)Environment.ProcessId);

    /// <summary>Called before each step that changes a file, by its name, and once the temp files are written ("written").
    /// Tests: an interruption there, or a fault in the temp files.</summary>
    internal Action<string>? Step { get; init; }

    /// <summary>The device a folder is on: a rename never crosses one. Tests: a stand-in.</summary>
    internal Func<string, (uint, uint)?> Device { get; init; } = path => Libc.Stat(path) is { } s ? (s.DevMajor, s.DevMinor) : null;

    /// <summary>The bytes free on the file system of a folder. Tests: a stand-in.</summary>
    internal Func<string, long> FreeSpace { get; init; } = path => new DriveInfo(path).AvailableFreeSpace;

    public Action<string>? Log { get; init; }

    /// <summary>The cache keys (as <see cref="NvidiaLinuxAppCache"/> names them) of the files a rewrite added.</summary>
    public IReadOnlySet<string> Added => _added;

    /// <summary>The cache folders holding a rewrite that couldn't be settled, each with why: no warm process may write there
    /// until <see cref="Recover"/> settles it.</summary>
    public IReadOnlyDictionary<string, string> Unfinished => _unfinished;

    /// <summary>Why the last <see cref="Run"/> left each set it didn't rewrite, by its .bin's path.</summary>
    public IReadOnlyDictionary<string, string> Left => _left;

    public const string Note = "rewriting NVIDIA's cache files so the game reads all of them", OpenElsewhere = "is open in another process";

    readonly HashSet<string> _added = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _unfinished = new(StringComparer.Ordinal), _left = new(StringComparer.Ordinal);
    const long BatchBytes = 32 << 20;

    string Root => NvidiaLinuxAppCache.CacheDir(gameDir);
    string Work => Path.Combine(gameDir, "proton-cache", "nodict");

    enum Outcome { Done, Left, Blocked }

    /// <summary>Settles each rewrite a commit file lists (<see cref="Settle"/>), and drops temp files no commit lists. Runs
    /// before a warm process: one could write to a file a commit names. Returns <see cref="Unfinished"/>: the folders whose
    /// rewrite it couldn't settle, left with their commit for the next time.</summary>
    public IReadOnlyDictionary<string, string> Recover()
    {
        _unfinished.Clear();
        if (!IsFolder(Path.Combine(gameDir, "proton-cache")) || !IsFolder(Work)) return _unfinished;
        foreach (var a in Folders(Work, 32))
        {
            foreach (var b in Folders(a.FullName, 16))
            {
                var live = Path.Combine(Root, a.Name, b.Name);
                var settled = true;
                foreach (var commit in b.EnumerateFiles("*", NoLinks).Where(f => f.Name.EndsWith(".commit", StringComparison.Ordinal)))
                    try
                    {
                        Log?.Invoke(Settle(ReadPlan(commit.FullName), commit.FullName, live) ? $"{commit.Name[..^7]}: finished rewriting it, which was interrupted"
                            : $"{commit.Name[..^7]}: a rewrite of it was interrupted before its files were in place, so it is as it was");
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        if (!IsFolder(live)) continue;   // the cache was cleared meanwhile: nothing to settle
                        settled = false;
                        _unfinished.TryAdd(live, e.Message);
                        Log?.Invoke($"{commit.Name[..^7]}: rewriting it can't be finished yet: {e.Message}");
                    }
                if (settled) Clear(b);
            }
            Remove(a);
        }
        Remove(new DirectoryInfo(Work));
        return _unfinished;
    }

    /// <summary>Rewrites each of the warm's sets that holds a dictionary, after <see cref="Recover"/>: in every cache folder of
    /// the game, since a pass cut short by Core's exit leaves sets no later process writes. <paramref name="note"/> gets
    /// <see cref="Note"/> before the first. Returns how many sets were rewritten.</summary>
    public int Run(Action<string>? note = null)
    {
        Recover();
        _left.Clear();
        var done = 0;
        var noted = false;
        foreach (var live in Folders().Where(l => !_unfinished.ContainsKey(l)))
            foreach (var n in Sets(live))
            {
                if (!HasDictionary(live, n)) continue;
                if (!noted) note?.Invoke(Note);
                noted = true;
                var outcome = Rewrite(live, n);
                if (outcome == Outcome.Done) done++;
                if (outcome == Outcome.Blocked) break;   // its commit names files of this folder until it is settled
            }
        return done;
    }

    /// <summary>The length and inode of each of the warm's .bin files the game reads, by path. <paramref name="strict"/>: a
    /// listing that fails throws, else it returns what it listed.</summary>
    public Dictionary<string, (long Length, ulong Inode)> Bins(bool strict = false)
    {
        var bins = new Dictionary<string, (long, ulong)>(StringComparer.Ordinal);
        try
        {
            foreach (var live in Folders())
                foreach (var n in Sets(live))
                    if (Path.Combine(live, Name(n) + ".bin") is var bin && Length(bin) is { } length && Libc.Stat(bin) is { } st) bins[bin] = (length, st.Inode);
        }
        catch (Exception e) when (!strict && e is IOException or UnauthorizedAccessException) { }   // a folder going meanwhile
        return bins;
    }

    /// <summary>The cache keys of the warm's sets in the folder (<see cref="NvidiaLinuxAppCache"/>), as <see cref="Added"/> names them.</summary>
    public IReadOnlySet<string> Keys() => Bins().Keys.Select(bin => (Live: Path.GetDirectoryName(bin)!, Set: Path.GetFileNameWithoutExtension(bin)))
        .Select(b => $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(gameDir))}/{Path.GetFileName(Path.GetDirectoryName(b.Live))}/{Path.GetFileName(b.Live)}/{b.Set}")
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether the set of <paramref name="bin"/> holds no dictionary, its .toc read whole.</summary>
    public static bool Plain(string bin)
    {
        try { return GlCacheFile.ReadToc(File.ReadAllBytes(Path.ChangeExtension(bin, ".toc")), out _) is { } t && !t.Records.Any(r => r.IsDictionary); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    List<string> Folders() =>
        IsFolder(Path.Combine(gameDir, "proton-cache")) && IsFolder(Path.Combine(gameDir, "proton-cache", "nvidia")) && IsFolder(Root)
            ? [.. from a in Folders(Root, 32) from b in Folders(a.FullName, 16) select b.FullName] : [];

    /// <summary>The numbers of the warm's sets in the folder that the game reads, each a .toc and a .bin.</summary>
    List<int> Sets(string dir) => [.. new DirectoryInfo(dir).EnumerateFiles("*", NoLinks).Where(f => f.Name.EndsWith(".toc", StringComparison.Ordinal))
        .Select(f => Number(f.Name)).OfType<int>().Where(n => n < Files && IsFile(Path.Combine(dir, Name(n) + ".bin"))).Order()];

    /// <summary>The set number of one of the warm's files, &lt;app name&gt;&lt;n&gt;.toc or .bin; null for any other name.</summary>
    int? Number(string file)
    {
        if (!file.StartsWith(appName, StringComparison.Ordinal) || !(file.EndsWith(".toc", StringComparison.Ordinal) || file.EndsWith(".bin", StringComparison.Ordinal))) return null;
        var digits = file[appName.Length..^4];
        return digits.Length is > 0 and <= 4 && digits.All(char.IsAsciiDigit) && (digits.Length == 1 || digits[0] != '0') ? int.Parse(digits, CultureInfo.InvariantCulture) : null;
    }

    string Name(int n) => appName + n.ToString(CultureInfo.InvariantCulture);

    bool HasDictionary(string live, int n)
    {
        var toc = Path.Combine(live, Name(n) + ".toc");
        try
        {
            if (GlCacheFile.ReadToc(File.ReadAllBytes(toc), out var why) is { } t) return t.Records.Any(r => r.IsDictionary);
            LeftAs(live, n, why!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LeftAs(live, n, e.Message); }
        return false;
    }

    void LeftAs(string live, int n, string why)
    {
        _left[Path.Combine(live, Name(n) + ".bin")] = why;
        Log?.Invoke($"{Name(n)}: left as it is, {why}");
    }

    Outcome Rewrite(string live, int n)
    {
        var name = Name(n);
        string toc = Path.Combine(live, name + ".toc"), bin = Path.Combine(live, name + ".bin");
        var temp = Path.Combine(Work, Path.GetFileName(Path.GetDirectoryName(live))!, Path.GetFileName(live));
        var clock = Stopwatch.StartNew();
        List<Output> outputs = [];
        Source? source = null;
        var committed = false;
        try
        {
            if (Held(toc) || Held(bin)) throw new Refused("a process has it open");
            if (NvidiaBytes() is var used && used > Budget) throw new Refused($"the game's NVIDIA cache holds {Format.Bytes(used)} already");
            if (new FileInfo(bin).Length > FileLimit) throw new Refused($"its .bin is past {Format.Bytes(FileLimit)}");   // the driver's offsets past it wrap
            source = Source.Read(toc, bin);
            foreach (var d in new[] { Work, Path.GetDirectoryName(temp)!, temp })
            {
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                if (!IsFolder(d)) throw new Refused($"{d} is a link");   // checked before the next one is made in it
            }
            if (Device(temp) is not { } t || t != Device(live)) throw new Refused($"{Work} isn't on the cache's file system");
            if (FreeSpace(temp) < 2 * source.BinLength + (1L << 30)) throw new Refused("the disk has too little free space");
            outputs = Write(source, live, temp, n);
            Step?.Invoke("written");
            var both = NvidiaBytes() + outputs.Sum(o => o.TocLength + o.BinLength);
            var after = both - source.TocLength - source.BinLength;
            if (after > Budget) throw new Refused($"rewritten, the game's NVIDIA cache would hold {Format.Bytes(after)}, more than {Format.Bytes(Budget)}");
            if (both > Cap) throw new Refused($"with both copies there for a moment, the game's NVIDIA cache would hold {Format.Bytes(both)}, more than the driver keeps ({Format.Bytes(Cap)})");
            Verify(source, outputs, temp);
            if (Held(toc) || Held(bin)) throw new Refused("a process opened it meanwhile");
            if (!source.Unchanged()) throw new Refused("it changed meanwhile");
            var plan = new Plan(new(name, source.TocLength, source.BinLength), [.. outputs.Select(o => new Plan.Set(o.Name, o.TocLength, o.BinLength))]);
            var commit = Commit(temp, plan);
            committed = true;
            Install(plan, commit, temp, live);
            Tidy(temp);
            var plain = outputs.Sum(o => o.Rewritten);
            Log?.Invoke($"{name}: {plain:N0} of {source.Records.Count - 1:N0} entries no longer need its dictionary, {Format.Bytes(source.BinLength)} now "
                + string.Join(" and ", outputs.Select(o => $"{Format.Bytes(o.BinLength)} in {o.Name}")) + $", in {clock.Elapsed.TotalSeconds:0.0} s");
            return Outcome.Done;
        }
        catch (Exception e) when (e is Refused or IOException or UnauthorizedAccessException)
        {
            foreach (var o in outputs) o.Dispose();
            if (committed)
            {
                _unfinished.TryAdd(live, e.Message);
                Log?.Invoke($"{name}: rewriting it stopped part way, settled at the next compile: {e.Message}");
                return Outcome.Blocked;
            }
            Discard(temp, name, outputs);
            LeftAs(live, n, e.Message);
            return Outcome.Left;
        }
        finally { source?.Dispose(); }
    }

    List<Output> Write(Source s, string live, string temp, int n)
    {
        bool Taken(string dir, int i) => File.Exists(Path.Combine(dir, Name(i) + ".toc")) || File.Exists(Path.Combine(dir, Name(i) + ".bin"));
        var free = new Queue<int>(Enumerable.Range(0, Files).Where(i => i != n && !Taken(live, i) && !Taken(temp, i)));
        var outputs = new List<Output>();
        try
        {
            if (!free.TryDequeue(out var first)) throw new Refused($"no file of the {Files} the game reads is free for it");
            outputs.Add(new Output(temp, Name(first), s));
            foreach (var batch in s.Batches())
            {
                var entries = new byte[batch.Count][];
                Each(batch.Count, s.Codecs, (i, c) => entries[i] = Plain(s, batch[i].Record, batch[i].Entry, c));
                for (var i = 0; i < batch.Count; i++)
                {
                    var o = outputs[^1];
                    if (o.BinLength + entries[i].Length > FileLimit)
                    {
                        if (o.Count == 0) throw new Refused("an entry is larger than a file may be");
                        if (!free.TryDequeue(out var m)) throw new Refused($"what passes {Format.Bytes(FileLimit)} has no free file of the {Files} the game reads");
                        o.Close();
                        outputs.Add(o = new Output(temp, Name(m), s));
                    }
                    o.Add(batch[i].Record.Key, entries[i], !ReferenceEquals(entries[i], batch[i].Entry));
                }
            }
            foreach (var o in outputs) o.Close();
            return outputs;
        }
        catch
        {
            foreach (var o in outputs) o.Dispose();
            Discard(temp, Name(n), outputs);
            throw;
        }
    }

    /// <summary>The entry as it goes into the rewritten file: the same array when it decodes without a dictionary.</summary>
    static byte[] Plain(Source s, Record r, byte[] entry, Codec c)
    {
        if (!GlCacheFile.Checks(entry, r)) throw new Refused("an entry doesn't match its .toc record or its checksum");
        var payload = entry.AsSpan(GlCacheFile.EntryHeaderSize);
        if (GlCacheFile.FrameOf(payload) is not { } f || f.Length != payload.Length - 4) throw new Refused("an entry isn't one zstd frame");
        if (f.ContentSize >= 0 && f.ContentSize != BinaryPrimitives.ReadUInt32LittleEndian(payload)) throw new Refused("an entry's frame holds another size than it says");
        if (f.DictId == 0 && c.Decodes(payload)) return entry;   // the driver's own bytes
        if (f.DictId != 0 && f.DictId != s.DictionaryId) throw new Refused($"an entry names dictionary {f.DictId:x8}, not its file's");
        var frame = c.Encode(c.Decode(payload, dictionary: true, 0));
        var plain = new byte[4 + frame.Length];
        payload[..4].CopyTo(plain);
        frame.CopyTo(plain.AsSpan(4));
        return GlCacheFile.Entry(r.Key, plain);
    }

    /// <summary>Reads the rewritten files back from disk and checks them against the original as the driver would read them:
    /// the same header, the original's keys in order without the dictionary, contiguous entries within <see cref="FileLimit"/>,
    /// each entry's four checks, each rewritten one a plain frame that decodes without a dictionary to the original's bytes,
    /// each other one the original's bytes.</summary>
    void Verify(Source s, List<Output> outputs, string temp)
    {
        var want = s.Records.Where(r => !r.IsDictionary).ToList();
        var got = new List<(int File, Record Record)>();
        var bins = new List<Microsoft.Win32.SafeHandles.SafeFileHandle>();
        try
        {
            foreach (var o in outputs)
            {
                var t = GlCacheFile.ReadToc(File.ReadAllBytes(Path.Combine(temp, o.Name + ".toc")), out var why) ?? throw new Refused($"its rewritten .toc doesn't read back: {why}");
                var h = File.OpenHandle(Path.Combine(temp, o.Name + ".bin"));
                bins.Add(h);
                var header = new byte[GlCacheFile.HeaderSize];
                if (!t.Header.SequenceEqual(s.Header) || RandomAccess.Read(h, header, 0) != header.Length || !header.SequenceEqual(s.Header))
                    throw new Refused("a rewritten file's header isn't the original's");
                long end = GlCacheFile.HeaderSize;
                foreach (var r in t.Records)
                {
                    if (r.Offset != end) throw new Refused("a rewritten file's entries aren't contiguous");
                    end += r.Length;
                    got.Add((bins.Count - 1, r));
                }
                if (end != RandomAccess.GetLength(h) || end > FileLimit) throw new Refused("a rewritten .bin isn't as long as its .toc says");
            }
            if (got.Count != want.Count || got.Where((g, i) => !g.Record.Key.AsSpan().SequenceEqual(want[i].Key)).Any())
                throw new Refused("the rewritten files don't hold the original's keys in order");
            using var source = File.OpenHandle(s.BinPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            for (var at = 0; at < want.Count;)
            {
                var batch = new List<(byte[] Was, byte[] Now)>();
                for (long bytes = 0; at < want.Count && bytes < BatchBytes; at++)
                {
                    batch.Add((Source.Entry(source, want[at], s.BinLength), Source.Entry(bins[got[at].File], got[at].Record, long.MaxValue)));
                    bytes += want[at].Length + got[at].Record.Length;
                }
                var first = at - batch.Count;
                Each(batch.Count, s.Codecs, (i, c) => Same(want[first + i], batch[i].Was, got[first + i].Record, batch[i].Now, c));
            }
        }
        finally { foreach (var h in bins) h.Dispose(); }
    }

    static void Same(Record was, byte[] wasEntry, Record now, byte[] nowEntry, Codec c)
    {
        if (!GlCacheFile.Checks(nowEntry, now)) throw new Refused("a rewritten entry doesn't match its .toc record or its checksum");
        if (!GlCacheFile.Checks(wasEntry, was)) throw new Refused("an original entry changed meanwhile");
        ReadOnlySpan<byte> before = wasEntry.AsSpan(GlCacheFile.EntryHeaderSize), after = nowEntry.AsSpan(GlCacheFile.EntryHeaderSize);
        if (nowEntry.AsSpan().SequenceEqual(wasEntry))
        {
            c.Decode(after, dictionary: false, 0);
            return;
        }
        if (GlCacheFile.FrameOf(after) is not { DictId: 0, Checksum: false } g || g.Length != after.Length - 4
            || g.ContentSize != BinaryPrimitives.ReadUInt32LittleEndian(after) || !after[..4].SequenceEqual(before[..4]))
            throw new Refused("a rewritten entry isn't one plain frame of its size");
        if (!c.Decode(after, dictionary: false, 0).SequenceEqual(c.Decode(before, dictionary: true, 1)))
            throw new Refused("a rewritten entry doesn't decode to the original's bytes");
    }

    /// <summary>A rewrite's commit file: the set rewritten, then the sets it became in key order, a line each of its name and
    /// its .toc's and .bin's lengths.</summary>
    sealed record Plan(Plan.Set Old, List<Plan.Set> New)
    {
        public readonly record struct Set(string Name, long Toc, long Bin);

        public string Text => string.Join('\n', new[] { Old }.Concat(New).Select(s => $"{s.Name} {s.Toc} {s.Bin}"));
    }

    string Commit(string temp, Plan p)
    {
        var path = Path.Combine(temp, p.Old.Name + ".commit");
        using (var f = new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.Write))
        {
            f.Write(Encoding.UTF8.GetBytes(p.Text));
            f.Flush(flushToDisk: true);
        }
        Step?.Invoke("commit");
        Rename(path + ".tmp", path);
        Libc.SyncFolder(temp);   // on disk before any file it lists takes its name
        return path;
    }

    /// <summary>The commit file's plan: only names of the warm's files, the rewritten set's its own.</summary>
    Plan ReadPlan(string commit)
    {
        var sets = new List<Plan.Set>();
        var invalid = new IOException($"{Path.GetFileName(commit)} doesn't list the warm's files");
        foreach (var line in File.ReadAllText(commit).Split('\n'))
        {
            var f = line.Split(' ');
            if (f.Length != 3 || Number(f[0] + ".toc") == null || sets.Any(x => x.Name == f[0])
                || !long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var toc) || toc < GlCacheFile.HeaderSize
                || !long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var bin) || bin < GlCacheFile.HeaderSize)
                throw invalid;
            sets.Add(new(f[0], toc, bin));
        }
        if (sets.Count < 2 || sets[0].Name + ".commit" != Path.GetFileName(commit)) throw invalid;
        return new(sets[0], sets[1..]);
    }

    /// <summary>Gives the new sets their names, each .bin before its .toc, then settles the rewrite.</summary>
    void Install(Plan p, string commit, string temp, string live)
    {
        foreach (var s in p.New)
            foreach (var ext in new[] { ".bin", ".toc" })
            {
                Step?.Invoke(s.Name + ext);
                Rename(Path.Combine(temp, s.Name + ext), Path.Combine(live, s.Name + ext));
            }
        Libc.SyncFolder(live);   // the new sets on disk before the original goes
        Settle(p, commit, live);
    }

    /// <summary>Ends a committed rewrite in one of two whole states. Every new set in place: the original goes. One not (cut
    /// short before its renames): the new sets go and the original stays. Deletes only files of the commit's lengths that no
    /// process has open, a .toc before its .bin, then the commit. True: the rewrite stands.</summary>
    bool Settle(Plan p, string commit, string live)
    {
        string At(string name, string ext) => Path.Combine(live, name + ext);
        if (new[] { p.Old }.Concat(p.New).SelectMany(s => new[] { At(s.Name, ".toc"), At(s.Name, ".bin") }).FirstOrDefault(f => File.Exists(f) && Held(f)) is { } held)
            throw new IOException($"{Path.GetFileName(held)} {OpenElsewhere}");
        bool Whole(Plan.Set s) => Length(At(s.Name, ".toc")) == s.Toc && Length(At(s.Name, ".bin")) == s.Bin;
        var stands = p.New.All(Whole);
        if (!stands && !Whole(p.Old)) throw new IOException($"{p.Old.Name}'s files changed since it was rewritten");
        var gone = (stands ? new List<Plan.Set> { p.Old } : p.New).SelectMany(s => new[] { (Path: At(s.Name, ".toc"), s.Toc), (Path: At(s.Name, ".bin"), s.Bin) })
            .Where(f => File.Exists(f.Path)).ToList();
        if (gone.FirstOrDefault(f => Length(f.Path) != f.Item2).Path is { } changed) throw new IOException($"{Path.GetFileName(changed)} changed since it was rewritten");
        foreach (var (f, _) in gone)
        {
            Step?.Invoke("delete " + Path.GetFileName(f));
            File.Delete(f);
        }
        Libc.SyncFolder(live);
        if (stands)
            foreach (var s in p.New) _added.Add($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(gameDir))}/{Path.GetFileName(Path.GetDirectoryName(live))}/{Path.GetFileName(live)}/{s.Name}");
        Step?.Invoke("settled");
        File.Delete(commit);
        return stands;
    }

    static long? Length(string path) => new FileInfo(path) is { Exists: true } f ? f.Length : null;

    static void Rename(string from, string to)
    {
        if (Libc.RenameNew(from, to) is var errno and not 0) throw new IOException($"naming {Path.GetFileName(to)} failed: {Marshal.GetPInvokeErrorMessage(errno)}");
    }

    void Discard(string temp, string name, List<Output> outputs)
    {
        if (!Directory.Exists(temp)) return;
        try
        {
            foreach (var o in outputs) { File.Delete(Path.Combine(temp, o.Name + ".toc")); File.Delete(Path.Combine(temp, o.Name + ".bin")); }
            File.Delete(Path.Combine(temp, name + ".commit.tmp"));
            Tidy(temp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Invoke($"{name}: its temp files stay until the next compile: {e.Message}"); }
    }

    /// <summary>Removes the temp folder and the ones above it up to proton-cache, those that are empty.</summary>
    void Tidy(string temp)
    {
        Remove(new DirectoryInfo(temp));
        Remove(new DirectoryInfo(Path.GetDirectoryName(temp)!));
        Remove(new DirectoryInfo(Work));
    }

    /// <summary>The game's NVIDIA folder, the one <see cref="NvidiaLinux.CacheSize"/> caps.</summary>
    internal string NvidiaDir => Path.Combine(gameDir, "proton-cache", "nvidia");

    /// <summary>Bytes of <see cref="NvidiaDir"/>.</summary>
    internal long NvidiaBytes() => new DirectoryInfo(NvidiaDir).EnumerateFiles("*", Recurse).Sum(f => f.Length);

    void Each(int count, Codecs codecs, Action<int, Codec> body)
    {
        try
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Threads }, codecs.Take, (i, _, c) => { body(i, c); return c; }, codecs.Give);
        }
        catch (AggregateException e)
        {
            var first = e.InnerExceptions[0];
            throw first as Refused ?? new Refused(first.Message);
        }
    }

    static IEnumerable<DirectoryInfo> Folders(string path, int hex) =>
        new DirectoryInfo(path).EnumerateDirectories("*", NoLinks).Where(d => d.Name.Length == hex && d.Name.All(char.IsAsciiHexDigitLower));

    static void Clear(DirectoryInfo dir)
    {
        foreach (var f in dir.EnumerateFiles("*", NoLinks)) f.Delete();
        Remove(dir);
    }

    static void Remove(DirectoryInfo dir)
    {
        try { dir.Delete(); }
        catch (IOException) { }   // not empty, or gone
    }

    static bool IsFolder(string path) => new DirectoryInfo(path) is { Exists: true } d && !d.Attributes.HasFlag(FileAttributes.ReparsePoint);

    static bool IsFile(string path) => new FileInfo(path) is { Exists: true } f && !f.Attributes.HasFlag(FileAttributes.ReparsePoint);

    static readonly EnumerationOptions NoLinks = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
    // an unreadable folder throws rather than go uncounted
    static readonly EnumerationOptions Recurse = new() { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>Why a set is left as it is.</summary>
    sealed class Refused(string why) : Exception(why);

    /// <summary>The set as it was read: its header, records, dictionary and lengths.</summary>
    sealed class Source : IDisposable
    {
        public required string TocPath { get; init; }
        public required string BinPath { get; init; }
        public required byte[] Toc { get; init; }
        public required byte[] Header { get; init; }
        public required List<Record> Records { get; init; }
        public required long BinLength { get; init; }
        public byte[]? Dictionary { get; init; }
        public uint DictionaryId { get; init; }
        public long TocLength => Toc.Length;

        /// <summary>The workers' zstd contexts, with the set's dictionary.</summary>
        public Codecs Codecs => _codecs ??= new(Dictionary);
        Codecs? _codecs;

        public void Dispose() => _codecs?.Dispose();

        public static Source Read(string toc, string bin)
        {
            var bytes = File.ReadAllBytes(toc);
            var t = GlCacheFile.ReadToc(bytes, out var why) ?? throw new Refused(why!);
            using var h = File.OpenHandle(bin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = RandomAccess.GetLength(h);
            var header = new byte[GlCacheFile.HeaderSize];
            if (RandomAccess.Read(h, header, 0) != header.Length || !header.SequenceEqual(t.Header)) throw new Refused("its .bin header isn't its .toc's");
            var dicts = t.Records.Where(r => r.IsDictionary).ToList();
            if (dicts.Count > 1) throw new Refused($"it holds {dicts.Count} dictionaries");
            byte[]? dict = null;
            uint id = 0;
            if (dicts.Count == 1)
            {
                var entry = Entry(h, dicts[0], length);
                if (!GlCacheFile.Checks(entry, dicts[0])) throw new Refused("its dictionary entry doesn't check out");
                dict = entry[GlCacheFile.EntryHeaderSize..];
                id = GlCacheFile.DictionaryId(dict) ?? throw new Refused("its dictionary entry isn't a zstd dictionary");
            }
            return new() { TocPath = toc, BinPath = bin, Toc = bytes, Header = t.Header, Records = t.Records, BinLength = length, Dictionary = dict, DictionaryId = id };
        }

        /// <summary>The entry <paramref name="r"/> names in the .bin, its header and payload.</summary>
        public static byte[] Entry(Microsoft.Win32.SafeHandles.SafeFileHandle bin, Record r, long length)
        {
            if (r.Offset < GlCacheFile.HeaderSize || r.Offset + r.Length > length) throw new Refused("an entry lies outside its .bin");
            var entry = new byte[r.Length];
            for (var at = 0; at < entry.Length;)
            {
                var n = RandomAccess.Read(bin, entry.AsSpan(at), r.Offset + at);
                if (n == 0) throw new Refused("its .bin ends inside an entry");
                at += n;
            }
            return entry;
        }

        /// <summary>The entries other than the dictionary, read in order, in batches of about <see cref="BatchBytes"/>.</summary>
        public IEnumerable<List<(Record Record, byte[] Entry)>> Batches()
        {
            using var h = File.OpenHandle(BinPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var batch = new List<(Record, byte[])>();
            long bytes = 0;
            foreach (var r in Records.Where(r => !r.IsDictionary))
            {
                batch.Add((r, Entry(h, r, BinLength)));
                if ((bytes += r.Length) < BatchBytes) continue;
                yield return batch;
                (batch, bytes) = ([], 0);
            }
            if (batch.Count > 0) yield return batch;
        }

        /// <summary>No process wrote to the set since it was read.</summary>
        public bool Unchanged() => File.ReadAllBytes(TocPath).AsSpan().SequenceEqual(Toc) && new FileInfo(BinPath).Length == BinLength;
    }

    /// <summary>One rewritten file set being written to its temp files.</summary>
    sealed class Output : IDisposable
    {
        readonly string _temp;
        readonly Source _source;
        readonly UnixFileMode _mode;
        FileStream? _bin;
        readonly MemoryStream _toc = new();

        public string Name { get; }
        public int Count { get; private set; }
        public int Rewritten { get; private set; }
        public long BinLength { get; private set; } = GlCacheFile.HeaderSize;
        public long TocLength => _toc.Length;

        public Output(string temp, string name, Source source)
        {
            (_temp, Name, _source) = (temp, name, source);
            if (!OperatingSystem.IsWindows()) _mode = File.GetUnixFileMode(source.BinPath);
            _bin = Create(Path.Combine(temp, name + ".bin"));
            _bin.Write(source.Header);
            _toc.Write(source.Header);
        }

        FileStream Create(string path)
        {
            var o = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, BufferSize = 1 << 20 };
            if (!OperatingSystem.IsWindows()) o.UnixCreateMode = _mode;
            return new(path, o);
        }

        public void Add(byte[] key, byte[] entry, bool rewritten)
        {
            Span<byte> record = stackalloc byte[GlCacheFile.RecordSize];
            key.CopyTo(record);
            BinaryPrimitives.WriteUInt32LittleEndian(record[16..], (uint)BinLength);
            BinaryPrimitives.WriteUInt32LittleEndian(record[20..], (uint)(entry.Length - GlCacheFile.EntryHeaderSize));
            _bin!.Write(entry);
            _toc.Write(record);
            BinLength += entry.Length;
            Count++;
            if (rewritten) Rewritten++;
        }

        /// <summary>Writes the .toc and puts both on disk, with the original's write time.</summary>
        public void Close()
        {
            if (_bin == null) return;
            _bin.Flush(flushToDisk: true);
            _bin.Dispose();
            _bin = null;
            var toc = Path.Combine(_temp, Name + ".toc");
            using (var f = Create(toc))
            {
                _toc.Position = 0;
                _toc.CopyTo(f);
                f.Flush(flushToDisk: true);
            }
            var time = File.GetLastWriteTimeUtc(_source.BinPath);
            File.SetLastWriteTimeUtc(toc, time);
            File.SetLastWriteTimeUtc(Path.Combine(_temp, Name + ".bin"), time);
        }

        public void Dispose() => _bin?.Dispose();
    }

    /// <summary>Codecs for the workers, kept from one batch to the next: each loads the dictionary.</summary>
    sealed class Codecs(byte[]? dictionary) : IDisposable
    {
        readonly System.Collections.Concurrent.ConcurrentBag<Codec> _idle = [];
        readonly List<Codec> _all = [];

        public Codec Take()
        {
            if (_idle.TryTake(out var c)) return c;
            c = new Codec(dictionary);
            lock (_all) _all.Add(c);
            return c;
        }

        public void Give(Codec c) => _idle.Add(c);

        public void Dispose()
        {
            foreach (var c in _all) c.Dispose();
        }
    }

    /// <summary>A worker's zstd contexts and buffers.</summary>
    sealed class Codec : IDisposable
    {
        readonly Decompressor _plain = new();
        readonly Decompressor? _dict;
        readonly Compressor _encoder = new(1);
        readonly byte[][] _raw = [[], []];
        byte[] _frame = [];

        public Codec(byte[]? dictionary)
        {
            if (dictionary != null)
            {
                _dict = new Decompressor();
                _dict.LoadDictionary(dictionary);
            }
            _encoder.SetParameter(ZSTD_cParameter.ZSTD_c_contentSizeFlag, 1);
            _encoder.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 0);
            _encoder.SetParameter(ZSTD_cParameter.ZSTD_c_dictIDFlag, 0);
        }

        /// <summary>The payload's frame decoded, through the file's dictionary or none, into buffer <paramref name="slot"/>:
        /// exactly as many bytes as its u32 says.</summary>
        public ReadOnlySpan<byte> Decode(ReadOnlySpan<byte> payload, bool dictionary, int slot)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            if (size > int.MaxValue / 2) throw new Refused("an entry says it holds more than 1 GiB");
            if (_raw[slot].Length < size) _raw[slot] = new byte[Math.Max(size, 2 * _raw[slot].Length)];
            var into = _raw[slot].AsSpan(0, (int)size);
            int n;
            try { n = (dictionary ? _dict ?? throw new Refused("an entry names a dictionary its file lacks") : _plain).Unwrap(payload[4..], into); }
            catch (ZstdException e) { throw new Refused($"an entry doesn't decode: {e.Message}"); }
            if (n != size) throw new Refused("an entry decodes to another size than it says");
            return into;
        }

        /// <summary>Whether the payload's frame decodes without a dictionary to as many bytes as its u32 says.</summary>
        public bool Decodes(ReadOnlySpan<byte> payload)
        {
            try
            {
                Decode(payload, dictionary: false, 0);
                return true;
            }
            catch (Refused) { return false; }
        }

        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> raw)
        {
            var bound = Compressor.GetCompressBound(raw.Length);
            if (_frame.Length < bound) _frame = new byte[bound];
            return _frame.AsSpan(0, _encoder.Wrap(raw, _frame));
        }

        public void Dispose()
        {
            _plain.Dispose();
            _dict?.Dispose();
            _encoder.Dispose();
        }
    }
}
