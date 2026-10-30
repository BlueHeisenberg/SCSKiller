using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.App;
using SCSKiller.Core.Warming;
using ZstdSharp;
using FileId = (uint DevMajor, uint DevMinor, ulong Inode);
using Record = SCSKiller.Core.Vendors.GlCacheFile.Record;

namespace SCSKiller.Core.Vendors;

/// <summary>SCSKiller's entries under the leaf's one dictionary (<see cref="SteamEntryCoding.LeafDictionary"/>). The driver
/// reads every read-only file of a leaf with one dictionary, the first one's in the order it reads them that holds one, and
/// an entry under any other misses (measured). So each file SCSKiller writes in the leaf carries that dictionary's entry,
/// byte for byte, before SCSKiller's entries, and each of them is encoded with it. The dictionary is the one Steam's files
/// hold, else the one SCSKiller placed in the leaf (<see cref="DictFile"/>), else one trained from the warm's entries. When
/// none can be had (<see cref="NoDictionary"/>) the entries go in stored, as the warm has them. Steam's merge keeps the first
/// copy of a key, the game's fragments first, so a fragment's dictionary would replace the merged file's: the up-to-date
/// look checks it and places again. A dictionary SCSKiller placed in the leaf is permanent, as the game's entries may use
/// it at any time: while one it recorded (<see cref="RecordedHashes"/>) is in the leaf or kept in the game's data folder,
/// no other is chosen or trained, the placement reuses it or stores the entries with it left where it is, and no file that
/// held it loses its last copy (<see cref="KeepRecorded"/>): removal carries that into Steam's merged file or refuses.
/// Anything else unexpected refuses the placement.</summary>
sealed partial class SteamCachePlace
{
    /// <summary>The threads that encode the warm's entries.</summary>
    public int Threads { get; init; } = Environment.ProcessorCount;

    /// <summary>The priority they and the training run at, the warm's: nice 10 below normal, nice 19 and the idle I/O class idle.</summary>
    public WarmPriority Priority { get; init; } = WarmPriority.Idle;

    // level 19 when the entries don't fit at 9: 1.84 times smaller than stored against 1.65, and 10 times slower (measured)
    const int Level = 9, TightLevel = 19;
    const int DictCapacity = 2 << 20;   // the driver's own dictionaries are 2 MiB
    const int DictSamples = 4000;
    const long Batch = 64L << 20;       // the warm's bytes, as stored, encoded at a time
    const string FromSteam = "Steam's", FromKept = "ours kept", FromTrained = "ours trained";

    /// <summary>The dictionary SCSKiller's entries in the leaf are encoded with: its id, the SHA-256 of the dictionary, its
    /// entry's checksum and size, where it came from, and the level the entries were encoded at.</summary>
    sealed record DictPlaced(uint Id, string Hash, ulong Sum, uint Size, string Source, int Level);

    /// <summary>The leaf's dictionary as an entry, byte for byte as every file SCSKiller writes there carries it.</summary>
    sealed record LeafDict(byte[] Entry, uint Id, string Source)
    {
        public string Hash { get; } = Convert.ToHexString(SHA256.HashData(Entry.AsSpan(GlCacheFile.EntryHeaderSize)));
        public ulong Sum => BinaryPrimitives.ReadUInt64LittleEndian(Entry.AsSpan(20));
        public uint Size => (uint)(Entry.Length - GlCacheFile.EntryHeaderSize);
        public ReadOnlySpan<byte> Dictionary => Entry.AsSpan(GlCacheFile.EntryHeaderSize);
        public DictPlaced Placed(int level) => new(Id, Hash, Sum, Size, Source, level);
    }

    /// <summary>A leaf's dictionary entry SCSKiller encodes its entries with, in the game's data folder, named by its hash: the
    /// one a manifest names is never written over.</summary>
    string DictFile(string leaf, string hash) => Path.Combine(gameDir, $"steam-place.{leaf.Replace('/', '.')}.{hash[..16].ToLowerInvariant()}.dict");

    /// <summary>The key the driver finds a leaf's dictionary under: the files' header bytes 8 to 16, then "_NVDICT_" (measured).</summary>
    static byte[] DictKey(byte[] header) => [.. header.AsSpan(8, 8), .. "_NVDICT_"u8];

    /// <summary>What a pass encoded and copied.</summary>
    sealed class Stats
    {
        public int Encoded, Copied;
        public long Raw;
    }

    /// <summary>No dictionary can be had that every file SCSKiller writes in the leaf may carry: the placement stores the
    /// entries as the warm has them. <see cref="Missing"/>: what Steam's files held then, for the up-to-date look.</summary>
    sealed class NoDictionary(string why) : Exception(why)
    {
        public string? Missing { get; set; }
    }

    /// <summary>The placement as <see cref="Coding"/> says; stored as the warm has them when the leaf's dictionary can't be had.</summary>
    SteamPlaceResult PlaceCoded(Manifest? m, string target, string leaf, WarmSets sets, CancellationToken ct)
    {
        if (Coding != SteamEntryCoding.LeafDictionary) return PlaceNvidia(m, target, leaf, sets, ct);
        try { return PlaceNvidia(m, target, leaf, sets, ct); }
        catch (NoDictionary e)
        {
            Log?.Invoke($"{leaf}: the pipelines go in as the warm stores them, {e.Message}");
            return PlaceNvidia(m, target, leaf, sets, ct, e);
        }
    }

    /// <summary>Writes the temp files of <paramref name="outs"/> with the warm's entries (<paramref name="warm"/>, in order)
    /// under the leaf's dictionary at <see cref="Level"/>, or at <see cref="TightLevel"/> when some don't fit at it. An entry
    /// the leaf holds already under that dictionary is copied, the others are encoded from the warm's sets. Returns what fits
    /// no file, and the dictionary; null when no entry needs one.</summary>
    (List<(int Set, Record Record)> Left, DictPlaced? Dict) PlaceEncoded(Manifest? m, OwnerNote? note, Folder dir, string leaf, List<Out> outs, LeafFiles files,
        HashSet<string> recorded, HashSet<uint> framed, WarmSets sets, List<(int Set, Record Record)> warm, HashSet<FileId> known, CancellationToken ct)
    {
        using var lowered = new Lowered(Math.Max(1, Threads), Priority);
        try { return Encode(m, note, dir, leaf, outs, files, recorded, framed, sets, warm, known, lowered, ct); }
        catch (NoDictionary e)
        {
            try
            {
                e.Missing = Missing(outs.Where(o => o.Target.SteamFirst || o.SteamOwns).Select(o => (o.Target.Name, o.Existing,
                    o.Target.SteamFirst && m?.Target == dir.Path && m.Files.FirstOrDefault(f => f.Name == o.Target.Name) is { Keys: > 0 } ? ReadOurs(o.Target.Name) : [])));
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { e.Missing = null; }
            throw;
        }
    }

    (List<(int Set, Record Record)> Left, DictPlaced? Dict) Encode(Manifest? m, OwnerNote? note, Folder dir, string leaf, List<Out> outs, LeafFiles files,
        HashSet<string> recorded, HashSet<uint> framed, WarmSets sets, List<(int Set, Record Record)> warm, HashSet<FileId> known, Lowered lowered, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var dict = ChooseDictionary(m, note, dir, leaf, outs, files, recorded, framed, sets, warm, lowered, ct);
        if (dict != null) SaveDictionary(leaf, dict);
        // a placement cut short may have left files of either dictionary
        var same = dict != null && m is { Pending: false, Dictionary: { } was } && m.Target == dir.Path && was.Hash == dict.Hash;
        if (dict != null && m is { Pending: true } && m.Target == dir.Path && warm.Count > 0)
            Log?.Invoke($"{leaf}: the last placement there didn't finish, every pipeline is encoded again");
        var copies = same ? Copies(outs) : [];
        var stats = new Stats();
        var level = Level;
        var at = Pass(copies);
        if (at < warm.Count)
        {
            Log?.Invoke($"{warm.Count - at:N0} pipelines don't fit at zstd level {Level}: encoded at level {TightLevel}");
            CleanUp(dir, known);
            (level, stats) = (TightLevel, new Stats());
            at = Pass(same && m!.Dictionary!.Level == TightLevel ? copies : []);
        }
        if (dict != null)
        {
            // a file left as it is holds the dictionary or not: the up-to-date look checks it still does
            foreach (var o in outs.Where(o => !o.Write)) o.Dict = Holds(o.Existing, dict);
            if (outs.Any(o => o.Target.Slot && o.Warm.Count > 0) && !outs.Any(o => !o.Target.Slot && o.Dict))
                throw new NoDictionary($"no file the driver reads the dictionary {dict.Id:x8} from would hold it");
            Log?.Invoke($"{leaf}: zstd level {level} under dictionary {dict.Id:x8} ({dict.Source}), {stats.Encoded:N0} pipelines encoded "
                + $"({Format.Bytes(stats.Raw)} raw) and {stats.Copied:N0} copied in {clock.Elapsed.TotalSeconds:0.0} s, "
                + $"{(Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalSeconds:0} s of CPU on {Threads} threads");
        }
        Permanent(outs, recorded, dict);
        return ([.. warm.Skip(at)], dict?.Placed(level));

        int Pass(Dictionary<Key, (Out From, Record Record)> from)
        {
            GlCacheDict? codec;
            try { codec = dict != null ? new GlCacheDict(dict.Dictionary, level) : null; }
            catch (InvalidDataException e) when (dict!.Source != FromKept) { throw new NoDictionary($"the dictionary {dict.Id:x8} doesn't load: {e.Message}"); }
            using (codec) return Fill(dir, outs, sets, warm, dict, codec, from, known, lowered, stats, ct);
        }
    }

    /// <summary>One pass of <see cref="PlaceEncoded"/>: fills the files in order, each with the next entries while they fit,
    /// the dictionary entry before the first. Returns the index in <paramref name="warm"/> of the first entry that fits none.</summary>
    int Fill(Folder dir, List<Out> outs, WarmSets sets, List<(int Set, Record Record)> warm, LeafDict? dict, GlCacheDict? codec,
        Dictionary<Key, (Out From, Record Record)> copies, HashSet<FileId> known, Lowered lowered, Stats stats, CancellationToken ct)
    {
        var header = sets.All[0].Header;
        byte[]?[] batch = [];
        int at = 0, from = 0;
        foreach (var o in outs.Where(o => !o.Kept))
        {
            Reset(o);
            Sink? s = null;
            try
            {
                for (; at < warm.Count; at++)
                {
                    if (at - from >= batch.Length)
                    {
                        if (Running()) throw new Refused(SteamPlaceState.GameRunning, "the game started");
                        (from, batch) = (at, Entries(warm, at, sets, dict!, codec!, copies, lowered, stats, ct));
                    }
                    if (batch[at - from] is not { } entry)
                    {
                        o.Skipped.Add(warm[at]);
                        continue;
                    }
                    s ??= Open(dir, o, dict, header, known, ct);
                    var lead = s.NeedsDict ? dict!.Entry : [];
                    if (s.At + lead.Length + entry.Length > o.Target.Limit) break;
                    if (lead.Length > 0) Put(s, o, lead, dictionary: true);
                    Put(s, o, entry);
                    o.Warm.Add(warm[at]);
                }
                o.Write |= o.Warm.Count > 0 || o.Ours;
                // a file written anew that held the leaf's dictionary keeps it, with none of the warm's entries too
                var keep = dict != null && o.Write && o.Warm.Count == 0 && Holds(o.Existing, dict);
                if (!o.Write || o.Count == 0 && !keep) continue;   // nothing to write, or Commit moves the file aside: its temp files go with the run's
                s ??= Open(dir, o, dict, header, known, ct);
                if (o.Warm.Count == 0 && s.NeedsDict && (s.NamesLeaf || keep)) Put(s, o, dict!.Entry, dictionary: true);
                s.Close(dir[TempPrefix + o.Target.Name + ".toc"], known);
                (o.End, o.Written) = (s.At, s.At - GlCacheFile.HeaderSize - o.SteamBytes);
                if (o.Same) o.Write = false;
            }
            finally { s?.Dispose(); }
        }
        return at;
    }

    /// <summary>The file as <see cref="Fill"/> starts it: Steam's entries alone, and SCSKiller's earlier ones only if it
    /// held some.</summary>
    static void Reset(Out o)
    {
        o.Warm.Clear();
        o.WarmPlaced.Clear();
        o.Skipped.Clear();
        o.End = GlCacheFile.HeaderSize + o.SteamBytes;
        o.Write = o.Target.SteamFirst && o.Mine.Count > 0;
        (o.Dict, o.DictOurs, o.Written, o.Same) = (false, false, null, false);
        (o.TocWritten, o.BinWritten) = (null, null);
    }

    /// <summary>One line per placement for the warm's entries it left out, by set.</summary>
    void LogSkipped(string leaf, List<Out> outs, WarmSets sets)
    {
        var skipped = outs.SelectMany(o => o.Skipped).ToList();
        if (skipped.Count > 0)
            Log?.Invoke($"{leaf}: {skipped.Count:N0} of the warm's pipelines skipped, they don't check or don't decode: "
                + string.Join(", ", skipped.GroupBy(w => sets.All[w.Set].Name).Select(g => $"{g.Count():N0} in {g.Key}")));
    }

    static void Put(Sink s, Out o, byte[] entry, bool dictionary = false)
    {
        s.Add(entry);
        o.WarmPlaced.Add(new(Key.Of(entry.AsSpan(4, 16)), BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(20))));
        if (dictionary) (o.DictOurs, o.Dict) = (true, true);
    }

    /// <summary>The next batch of entries from <paramref name="first"/> on, as they go into the files: copied from the leaf
    /// when SCSKiller placed them there under the dictionary, else encoded from the warm's stored entries. Null for one of the
    /// warm's that doesn't check or doesn't decode, which is skipped. Refused when one SCSKiller placed doesn't check or names
    /// another dictionary.</summary>
    byte[]?[] Entries(List<(int Set, Record Record)> warm, int first, WarmSets sets, LeafDict dict, GlCacheDict codec,
        Dictionary<Key, (Out From, Record Record)> copies, Lowered lowered, Stats stats, CancellationToken ct)
    {
        var end = first;
        for (long bytes = 0; end < warm.Count && bytes < Batch; end++) bytes += warm[end].Record.Length;
        var entries = new byte[]?[end - first];
        try
        {
            Parallel.For(0, entries.Length, new ParallelOptions { MaxDegreeOfParallelism = lowered.MaximumConcurrencyLevel, TaskScheduler = lowered, CancellationToken = ct }, codec.Take, (i, _, w) =>
            {
                var (set, r) = warm[first + i];
                if (copies.TryGetValue(Key.Of(r.Key), out var c))
                {
                    if (Whole(c.From.Existing.Bin!, c.Record) is not { } have || !GlCacheFile.Checks(have, c.Record) || GlCacheFile.FrameDictId(have.AsSpan(GlCacheFile.EntryHeaderSize)) != dict.Id)
                        throw new Refused(SteamPlaceState.Failed, $"an entry of SCSKiller's in {c.From.Target.Name} isn't one it placed under the dictionary {dict.Id:x8}");
                    entries[i] = have;
                    Interlocked.Increment(ref stats.Copied);
                    return w;
                }
                // the warm's reject log has it: the others still go in
                if (Whole(sets.All[set].Bin, r) is not { } stored || !GlCacheFile.Checks(stored, r) || !w.Raw(stored.AsSpan(GlCacheFile.EntryHeaderSize), out var raw)) return w;
                entries[i] = w.Entry(r.Key, raw);
                Interlocked.Increment(ref stats.Encoded);
                Interlocked.Add(ref stats.Raw, raw.Length);
                return w;
            }, codec.Give);
        }
        catch (AggregateException e) { ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw(); }
        return entries;
    }

    /// <summary>SCSKiller's entries the leaf holds that can be copied when the dictionary is the same: those of its own file
    /// and its earlier ones in Steam's, by key.</summary>
    static Dictionary<Key, (Out From, Record Record)> Copies(List<Out> outs)
    {
        var copies = new Dictionary<Key, (Out, Record)>();
        foreach (var o in outs.Where(o => !o.Kept && o.Existing is { Bin: not null, Broken: null }))
            foreach (var r in o.Target.SteamFirst ? o.Mine : o.Ours ? o.Existing.Records : [])
                if (!r.IsDictionary) copies.TryAdd(Key.Of(r.Key), (o, r));
        return copies;
    }

    /// <summary>The entry <paramref name="r"/> names, header and payload; null when the file ends inside it.</summary>
    static byte[]? Whole(SafeFileHandle bin, Record r)
    {
        var entry = new byte[r.Length];
        for (var at = 0; at < entry.Length;)
        {
            var n = RandomAccess.Read(bin, entry.AsSpan(at), r.Offset + at);
            if (n == 0) return null;
            at += n;
        }
        return entry;
    }

    /// <summary>Starts the file's temp .bin with Steam's entries, each checked against its record as it is copied.</summary>
    Sink Open(Folder dir, Out o, LeafDict? dict, byte[] header, HashSet<FileId> known, CancellationToken ct)
    {
        var name = o.Target.Name;
        var steamHeaders = o.Target.SteamFirst && o.Existing is { Exists: true, Broken: null };
        var (tocHeader, binHeader) = steamHeaders ? (o.Existing.TocHeader, o.Existing.BinHeader) : (header, header);
        var mode = o.Existing.Exists ? o.Existing.Mode : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var s = new Sink(o, () => Create(dir[TempPrefix + name + ".bin"], mode, known), tocHeader, binHeader, mode);
        try
        {
            var n = 0;
            foreach (var r in o.Steam)
            {
                if (++n % 4096 == 0) ct.ThrowIfCancellationRequested();
                // Steam's entries were checked before: one that isn't the same now changed meanwhile
                if (Whole(o.Existing.Bin!, r) is not { } entry) throw new Refused(SteamPlaceState.SteamBusy, $"Steam's {name} ends inside an entry");
                if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != GlCacheFile.EntryMagic || !entry.AsSpan(4, 16).SequenceEqual(r.Key)
                    || BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(28)) != r.Size)
                    throw new Refused(SteamPlaceState.SteamBusy, $"an entry of Steam's {name} isn't the one its .toc names");
                if (r.IsDictionary)
                {
                    s.SteamDict = true;
                    o.Dict |= dict != null && entry.AsSpan().SequenceEqual(dict.Entry);
                }
                else if (dict != null && !s.NamesLeaf && GlCacheFile.FrameDictId(entry.AsSpan(GlCacheFile.EntryHeaderSize)) == dict.Id) s.NamesLeaf = true;
                s.Add(entry);
            }
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    /// <summary>A file's temp .bin as entries go in, and its .toc, written by <see cref="Close"/>. While the entries are the
    /// file's own, byte for byte and in its order, nothing is written: a file that comes out the same keeps its name
    /// (<see cref="Out.Same"/>).</summary>
    sealed class Sink : IDisposable
    {
        readonly Func<FileStream> _create;
        readonly MemoryStream _toc = new();
        readonly byte[] _record;
        readonly byte[] _binHeader;
        readonly UnixFileMode _mode;
        FileStream? _bin;
        byte[] _have = new byte[1 << 16];
        int _count;

        public Sink(Out owner, Func<FileStream> create, byte[] tocHeader, byte[] binHeader, UnixFileMode mode)
        {
            (Owner, _create, _binHeader, _mode) = (owner, create, binHeader, mode);
            _record = new byte[GlCacheFile.RecordSizeOf(tocHeader)];
            _toc.Write(tocHeader);
            if (owner.Existing is not { Exists: true, Broken: null, TocRead: true, Bin: not null } e || !e.TocHeader.AsSpan().SequenceEqual(tocHeader)
                || !e.BinHeader.AsSpan().SequenceEqual(binHeader))
                Start();
        }

        public Out Owner { get; }
        public long At { get; private set; } = GlCacheFile.HeaderSize;
        public bool SteamDict { get; set; }   // Steam's entries hold a dictionary: SCSKiller adds none
        public bool NamesLeaf { get; set; }   // one of Steam's entries is encoded with the leaf's dictionary
        // the game's slot holds none: the driver decodes it with the read-only files' (measured), and Steam's fold would
        // put the slot's first, in front of the merged file's
        public bool NeedsDict => !SteamDict && !Owner.DictOurs && !Owner.Target.Slot;

        public void Add(ReadOnlySpan<byte> entry)
        {
            if (At + entry.Length > NvidiaLinux.CacheFileIndexed) throw new InvalidDataException($"{Owner.Target.Name} would come out past what its .toc indexes");
            GlCacheFile.WriteRecord(_record, entry.Slice(4, 16), At, entry.Length - GlCacheFile.EntryHeaderSize);
            _toc.Write(_record);
            if (_bin == null && !Holds(entry)) Start();
            _bin?.Write(entry);
            At += entry.Length;
            _count++;
        }

        /// <summary>The file's own entry at this place is <paramref name="entry"/>, byte for byte.</summary>
        bool Holds(ReadOnlySpan<byte> entry)
        {
            var records = Owner.Existing.Records;
            if (_count >= records.Count || records[_count] is var r && (r.Offset != At || r.Length != entry.Length)) return false;
            if (_have.Length < entry.Length) _have = new byte[Math.Max(entry.Length, 2 * _have.Length)];
            var have = _have.AsSpan(0, entry.Length);
            for (var read = 0; read < have.Length;)
            {
                var n = RandomAccess.Read(Owner.Existing.Bin!, have[read..], r.Offset + read);
                if (n == 0) return false;
                read += n;
            }
            return have.SequenceEqual(entry);
        }

        /// <summary>The temp .bin, with the entries so far, the file's own, copied from it.</summary>
        void Start()
        {
            _bin = _create();
            _bin.Write(_binHeader);
            for (long at = GlCacheFile.HeaderSize; at < At;)
            {
                var n = RandomAccess.Read(Owner.Existing.Bin!, _have.AsSpan(0, (int)Math.Min(_have.Length, At - at)), at);
                if (n == 0) throw new Refused(SteamPlaceState.SteamBusy, $"{Owner.Target.Name} changed meanwhile");
                _bin.Write(_have, 0, n);
                at += n;
            }
        }

        /// <summary>Puts the .bin on disk, then writes the .toc at <paramref name="toc"/>, synced, each recorded as written for
        /// <see cref="Commit"/> to hold. None when the file came out the same as it is: <see cref="Out.Same"/>.</summary>
        public void Close(string toc, HashSet<FileId> known)
        {
            if (_bin == null && _count == Owner.Existing.Records.Count && At == Owner.Existing.BinLength && _toc.Length == Owner.Existing.TocLength)
            {
                Owner.Same = true;
                return;
            }
            if (_bin == null) Start();
            _bin!.Flush(flushToDisk: true);
            Owner.BinWritten = Ino.Of(_bin.SafeFileHandle);
            using var f = Create(toc, _mode, known);
            _toc.Position = 0;
            _toc.CopyTo(f);
            f.Flush(flushToDisk: true);
            Owner.TocWritten = Ino.Of(f.SafeFileHandle);
        }

        public void Dispose() => _bin?.Dispose();
    }

    /// <summary>The leaf's dictionary: one SCSKiller recorded (<see cref="Recoverable"/>), the first the driver reads, else the
    /// one Steam's files hold, else one trained from <paramref name="warm"/>. Null when nothing is to be placed and none is
    /// there. NoDictionary when Steam's files hold one that doesn't read, or two, or have other header bytes than the warm's,
    /// or another than one SCSKiller recorded. While entries that stay in the leaf (<see cref="Staying"/>, the game's
    /// <paramref name="files"/> among them) are encoded with a dictionary of SCSKiller's that Steam's files don't hold, that
    /// one is the leaf's again, and the placement is refused when it can't be had or Steam's files hold another.</summary>
    LeafDict? ChooseDictionary(Manifest? m, OwnerNote? note, Folder dir, string leaf, List<Out> outs, LeafFiles files, HashSet<string> recorded, HashSet<uint> framed,
        WarmSets sets, List<(int Set, Record Record)> warm, Lowered lowered, CancellationToken ct)
    {
        var key = DictKey(sets.All[0].Header);
        var current = CurrentDictionary(m, note, dir, outs, key);
        string? problem = null;
        // the driver finds the dictionary under the files' own header bytes
        if (outs.FirstOrDefault(o => o.Target.SteamFirst && !o.Kept && o.Existing is { Exists: true, Broken: null } && !o.Existing.BinHeader.AsSpan(8, 8).SequenceEqual(key.AsSpan(0, 8))) is { } other)
            problem = $"Steam's {other.Target.Name} has another header than the warm's files";
        LeafDict? steam = null;
        var steamIds = new HashSet<uint>();
        // the dictionaries SCSKiller placed in the leaf, permanent there: in its files or kept, and the one a slot that stays
        // has its entries encoded with
        var kept = Recoverable(m, dir, leaf, outs, files, key, recorded);
        var recordedIds = RecordedIds(m, note, dir.Path);
        recordedIds.UnionWith(kept.Select(d => d.Id));
        recordedIds.UnionWith(framed);
        foreach (var o in outs.Where(o => o.Target.SteamFirst || o.SteamOwns))
        {
            if (o.Existing.Bin is not { } bin) continue;
            foreach (var r in (o.Kept ? o.Existing.Records : o.Steam).Where(r => r.IsDictionary))
            {
                var entry = r.Length <= MaxEntry ? Whole(bin, r) : null;
                if (entry != null && o.Kept && o.KeptOurs.Contains(new(Key.Of(r.Key), BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(20))))) continue;
                if (entry == null || !GlCacheFile.Checks(entry, r) || GlCacheFile.DictionaryId(entry.AsSpan(GlCacheFile.EntryHeaderSize)) is not { } id)
                    problem ??= $"the dictionary entry of Steam's {o.Target.Name} doesn't read";
                else if (!r.Key.AsSpan().SequenceEqual(key)) problem ??= $"Steam's {o.Target.Name} has its dictionary under another key than the warm's files";
                else
                {
                    // one with the id of SCSKiller's but other bytes is never put in its place
                    if (recordedIds.Contains(id) && !recorded.Contains(Convert.ToHexString(SHA256.HashData(entry.AsSpan(GlCacheFile.EntryHeaderSize)))))
                        throw new Refused(SteamPlaceState.Failed, $"{o.Target.Name} holds a dictionary with the id of SCSKiller's, {id:x8}, that isn't the one it recorded: left as it is");
                    steamIds.Add(id);
                    // a file SCSKiller writes would carry the other
                    if (steam != null && !entry.AsSpan().SequenceEqual(steam.Entry)) problem ??= $"Steam's files hold two dictionaries, {steam.Id:x8} and {id:x8}";
                    steam ??= new(entry, id, FromSteam);
                }
            }
        }
        // SCSKiller's dictionaries but one Steam's files keep
        var ours = Going(outs).Select(g => DictIdAt(g.O.Existing.Bin!, g.R)).OfType<uint>().ToHashSet();
        if (current is { } c) ours.Add(c.D.Id);
        ours.UnionWith(kept.Select(d => d.Id));
        ours.UnionWith(framed);
        ours.ExceptWith(steamIds);
        if (ours.Count > 0 && ours.Where(id => Framed(Staying(outs, files.Files), id)).ToList() is [var first, ..] needed)
        {
            if (needed.Count > 1 || current?.D.Id != first && kept.All(d => d.Id != first))
                throw new Refused(SteamPlaceState.Failed, $"the game's entries in Steam's folder use a dictionary of SCSKiller's, {first:x8}, that it has no record of: left as it is");
            if (problem != null || steam != null)
                throw new Refused(SteamPlaceState.Failed, $"the game's entries in Steam's folder use SCSKiller's dictionary {first:x8}, which can't stay the leaf's "
                    + $"({problem ?? $"Steam's files hold {steam!.Id:x8}"}): left as it is");
            return kept.FirstOrDefault(d => d.Id == first) ?? current?.Entry ?? PlacedDictionary(m, dir, outs, key)
                ?? throw new Refused(SteamPlaceState.Failed, $"the game's entries in Steam's folder use SCSKiller's dictionary {first:x8}, which can't be had: left as it is");
        }
        if (problem != null) throw new NoDictionary(problem);
        if (steam != null)
        {
            // never another than one SCSKiller placed: its entries go in stored, and it stays where it is
            if (kept.FirstOrDefault(d => d.Hash != steam.Hash) is { } placed)
                throw new NoDictionary($"Steam's files hold {steam.Id:x8}, and SCSKiller's dictionary {placed.Id:x8} stays in the leaf");
            return steam;
        }
        if ((kept.FirstOrDefault() ?? current?.Entry ?? PlacedDictionary(m, dir, outs, key)) is { } had) return had;
        return warm.Count > 0 ? Train(key, sets, warm, lowered, ct) : null;
    }

    /// <summary>The dictionaries SCSKiller recorded for the leaf (<paramref name="recorded"/>) that can be had: first those
    /// the leaf's files hold, whole and checking, the read-only ones in the order the driver reads them, then the game's
    /// (<paramref name="files"/>); then the manifest's and the one it placed before from the game's data folder, then any
    /// other kept there for the leaf.</summary>
    List<LeafDict> Recoverable(Manifest? m, Folder dir, string leaf, List<Out> outs, LeafFiles files, byte[] key, HashSet<string> recorded)
    {
        var found = new List<LeafDict>();
        void Add(LeafDict? d)
        {
            if (d != null && recorded.Contains(d.Hash) && found.All(f => f.Hash != d.Hash)) found.Add(d);
        }
        foreach (var e in outs.Select(o => o.Existing).Concat(files.Files).Where(e => e.Bin != null))
            foreach (var r in e.Records.Where(r => r.IsDictionary && r.Length <= MaxEntry))
                if (Whole(e.Bin!, r) is { } entry && GlCacheFile.Checks(entry, r)) Add(AsDictionary(entry, key, FromKept));
        if (m != null && m.Target == dir.Path)
            foreach (var d in (m.Pending ? new[] { m.WasDictionary, m.Dictionary } : [m.Dictionary, m.WasDictionary]).OfType<DictPlaced>())
                if (found.All(f => f.Hash != d.Hash)) Add(StoredDictionary(d, leaf, key));
        foreach (var (entry, _) in KeptDictionaries(leaf)) Add(AsDictionary(entry, key, FromKept));
        return found;
    }

    /// <summary>The dictionary entries kept in the game's data folder for the leaf, each whole and named by its hash.</summary>
    IEnumerable<(byte[] Entry, string Hash)> KeptDictionaries(string leaf)
    {
        if (!Directory.Exists(gameDir)) yield break;
        foreach (var path in Directory.EnumerateFiles(gameDir, $"steam-place.{leaf.Replace('/', '.')}.*.dict"))
        {
            if (!IsFile(path) || new FileInfo(path).Length is <= GlCacheFile.EntryHeaderSize or > MaxEntry) continue;
            var entry = File.ReadAllBytes(path);
            var hash = Convert.ToHexString(SHA256.HashData(entry.AsSpan(GlCacheFile.EntryHeaderSize)));
            if (path == DictFile(leaf, hash)) yield return (entry, hash);
        }
    }

    /// <summary>A record of <paramref name="e"/> holds <paramref name="dict"/>'s entry, byte for byte.</summary>
    static bool Holds(Existing e, LeafDict dict) =>
        e.Bin is { } bin && e.Records.Any(r => r.IsDictionary && r.Length == dict.Entry.Length && Whole(bin, r) is { } entry && entry.AsSpan().SequenceEqual(dict.Entry));

    /// <summary>A dictionary entry of the leaf's read-only files: the file, the dictionary's id, and its hash when it is one
    /// SCSKiller recorded (<see cref="RecordedCopy"/>).</summary>
    readonly record struct DictCopy(Out O, uint? Id, string? Hash);

    /// <summary>The dictionary entries of the leaf's read-only files in the order the driver reads them: as the files are, or
    /// once <paramref name="written"/>, with the entries of Steam's they keep, those carried (<see cref="Out.Carried"/>) and
    /// <paramref name="dict"/> where it goes.</summary>
    static List<DictCopy> Dictionaries(List<Out> outs, HashSet<string> recorded, bool written, LeafDict? dict = null)
    {
        var found = new List<DictCopy>();
        DictCopy Of(Out o, SafeFileHandle bin, Record r) => RecordedCopy(bin, r, recorded) is { } c ? new(o, c.Id, c.Hash) : new(o, DictIdAt(bin, r), null);
        foreach (var o in outs)
        {
            if (!written || !o.Write)
            {
                if (o.Existing.Bin is { } bin) found.AddRange(o.Existing.Records.Where(r => r.IsDictionary).Select(r => Of(o, bin, r)));
                continue;
            }
            if (o.Count == 0) continue;
            var steam = o.Steam.Where(r => r.IsDictionary).Select(r => (Bin: o.Existing.Bin!, Record: r));
            found.AddRange((o.Target.SteamFirst ? steam.Concat(o.Carried) : o.Carried.Concat(steam)).Select(c => Of(o, c.Bin, c.Record)));
            if (o.DictOurs && dict != null) found.Add(new(o, dict.Id, recorded.Contains(dict.Hash) ? dict.Hash : null));
        }
        return found;
    }

    /// <summary>Refused, everything left as it is, when the files once written would lose every copy of a dictionary
    /// SCSKiller recorded that the leaf holds, or the driver would read another than the recorded one it reads.</summary>
    static void Permanent(List<Out> outs, HashSet<string> recorded, LeafDict? dict)
    {
        var (before, after) = (Dictionaries(outs, recorded, written: false), Dictionaries(outs, recorded, written: true, dict));
        if (before.FirstOrDefault(b => b.Hash != null && after.All(a => a.Hash != b.Hash)) is { Hash: not null } lost)
            throw new Refused(SteamPlaceState.Failed, $"SCSKiller's dictionary {lost.Id:x8} would leave the leaf: left as it is");
        if (before.FirstOrDefault() is { Hash: { } first } was && after.FirstOrDefault().Hash != first)
            throw new Refused(SteamPlaceState.Failed, $"SCSKiller's dictionary {was.Id:x8} would stop being the leaf's: left as it is");
    }

    /// <summary>SCSKiller's dictionary in the leaf as <see cref="HeldDictionary"/> has it, with its entry when read from a file
    /// of SCSKiller's: checked there for its key, MurmurHash3, id and SHA-256.</summary>
    static (DictPlaced D, LeafDict? Entry)? CurrentDictionary(Manifest? m, OwnerNote? note, Folder dir, List<Out> outs, byte[] key)
    {
        if (HeldDictionary(m, note, dir, outs) is not { } held) return null;
        if (held.Entry == null) return (held.D, null);
        if (AsDictionary(held.Entry, key, FromKept) is not { } kept || kept.Id != held.D.Id || kept.Hash != held.D.Hash)
            throw new Refused(SteamPlaceState.Failed, "the cache SCSKiller placed in Steam's folder can't be checked: left as it is");
        return (held.D, kept);
    }

    /// <summary>The dictionary SCSKiller's files in the leaf hold, as recorded. By the manifest: its own once settled, else
    /// the one placed before; while pending, the one it replaces or the one it wrote, as SCSKiller's dictionary entries there
    /// are, else the one it replaces. Without it, by the owner note, either one it names, as SCSKiller's file of the warm's alone holds it. With
    /// the entry when read from a file. Refused when one of SCSKiller's entries is none of them, which is never trained
    /// over.</summary>
    static (DictPlaced D, byte[]? Entry)? HeldDictionary(Manifest? m, OwnerNote? note, Folder dir, List<Out> outs)
    {
        if (m != null && m.Target == dir.Path)
        {
            if (!m.Pending) return (m.Dictionary ?? m.WasDictionary) is { } d ? (d, null) : null;
            (DictPlaced D, byte[]? Entry)? held = null;
            foreach (var (o, r) in OurDictionaries(outs))
            {
                var entry = r.Length <= MaxEntry ? Whole(o.Existing.Bin!, r) : null;
                var hash = entry != null && GlCacheFile.Checks(entry, r) ? Convert.ToHexString(SHA256.HashData(entry.AsSpan(GlCacheFile.EntryHeaderSize))) : null;
                if (new[] { m.WasDictionary, m.Dictionary }.FirstOrDefault(x => x != null && x.Hash == hash) is not { } was || held is { } h && h.D.Hash != was.Hash)
                    throw new Refused(SteamPlaceState.Failed, $"SCSKiller's dictionary entry in {o.Target.Name} isn't the one it recorded");
                held ??= (was, entry);
            }
            if (held != null) return held;
            return m.WasDictionary is { } w ? (w, null) : null;
        }
        if (outs.FirstOrDefault(o => !o.Target.SteamFirst && o.Ours) is not { } own) return null;
        var dicts = own.Existing.Records.Where(r => r.IsDictionary).ToList();
        (uint? Id, string? Hash)[] named = note != null ? [(note.DictionaryId, note.DictionaryHash), (note.WasDictionaryId, note.WasDictionaryHash)] : [];
        if (dicts.Count == 0 && named.All(n => n is (null, null))) return null;
        if (dicts is not [var one] || own.Existing.Bin is not { } bin || one.Length > MaxEntry || Whole(bin, one) is not { } kept || !GlCacheFile.Checks(kept, one)
            || GlCacheFile.DictionaryId(kept.AsSpan(GlCacheFile.EntryHeaderSize)) is not { } id
            || Convert.ToHexString(SHA256.HashData(kept.AsSpan(GlCacheFile.EntryHeaderSize))) is var sha && !named.Contains(((uint?)id, (string?)sha)))
            throw new Refused(SteamPlaceState.Failed, "the cache SCSKiller placed in Steam's folder can't be checked: left as it is");
        return (new(id, sha, BinaryPrimitives.ReadUInt64LittleEndian(kept.AsSpan(20)), one.Size, FromKept, 0), kept);
    }

    /// <summary>SCSKiller's dictionary entries in the leaf: in its file of the warm's alone, and among its entries in Steam's.</summary>
    static IEnumerable<(Out O, Record R)> OurDictionaries(List<Out> outs) =>
        outs.Where(o => o.Existing.Bin != null)
            .SelectMany(o => (!o.Target.SteamFirst && o.Ours ? o.Existing.Records : o.Target.SteamFirst && !o.Kept ? o.Mine : []).Where(r => r.IsDictionary).Select(r => (o, r)));

    /// <summary>The leaf's files but <paramref name="outs"/>, read when the game's entries there decide what SCSKiller does or
    /// a placement looks for a dictionary it recorded: the game's own, which the driver writes while the game runs, each
    /// under the leaf's dictionary then. When a removal lets another dictionary come first, or a placement fills the game's
    /// own slot (<see cref="Watch"/>), with the folder's names they must be the same once its files are held to take their
    /// names: a game that ran meanwhile may have written entries under the one that came first, or into another slot.</summary>
    sealed class LeafFiles(Folder dir, List<Out> outs) : IDisposable
    {
        List<(string Name, Existing File)>? _read;
        List<string> _names = [];
        bool _watched;

        public IEnumerable<Existing> Files => (_read ??= Read()).Select(x => x.File);

        /// <summary>Takes the folder's names now, before the placement reads its files: a name that comes or goes after is
        /// refused at commit (<see cref="Check"/>), whatever is read. A new steamapp_shader_cache&lt;k&gt; is a new name.</summary>
        public void Watch() => (_names, _watched) = (Names(), true);

        List<(string Name, Existing File)> Read()
        {
            if (!_watched) _names = Names();
            var read = new List<(string, Existing)>();
            try
            {
                foreach (var name in _names.Where(n => n.EndsWith(".toc", StringComparison.Ordinal)).Select(n => n[..^4]).Where(n => outs.All(o => o.Target.Name != n)))
                    read.Add((name, Existing.Read(dir.At, name)));
                return read;
            }
            catch
            {
                foreach (var (_, e) in read) e.Dispose();
                throw;
            }
        }

        /// <summary>Every name in the folder but SCSKiller's own (<see cref="OwnPrefix"/>).</summary>
        List<string> Names() =>
            [.. new DirectoryInfo(dir.At).EnumerateFileSystemInfos().Select(f => f.Name).Where(n => !n.StartsWith(OwnPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

        /// <summary>Refused when the folder's names, a file read, or a file of <paramref name="outs"/> that isn't written,
        /// changed since they were read or watched; nothing to check when none was.</summary>
        public void Check(string? leaf)
        {
            if (_read == null && !_watched) return;
            if (!Names().SequenceEqual(_names) || (_read ?? []).Any(x => !x.File.Unchanged(dir.At, x.Name))
                || outs.Any(o => !o.Write && !o.Existing.Unchanged(dir.At, o.Target.Name)))
                throw new Refused(SteamPlaceState.SteamBusy, "the game's files in Steam's folder changed meanwhile", leaf);
        }

        public void Dispose()
        {
            foreach (var (_, e) in _read ?? []) e.Dispose();
        }
    }

    /// <summary>The entries that stay in the leaf whatever SCSKiller writes: Steam's, the game's, and SCSKiller's in a file
    /// left as it is.</summary>
    static List<(SafeFileHandle Bin, IEnumerable<Record> Records)> Staying(List<Out> outs, IEnumerable<Existing> others) =>
    [
        .. outs.Where(o => o.Existing.Bin != null && !(o.Write && !o.Target.SteamFirst)).Select(o => (o.Existing.Bin!, o.Write ? o.Steam : (IEnumerable<Record>)o.Existing.Records)),
        .. others.Where(e => e.Bin != null).Select(e => (e.Bin!, (IEnumerable<Record>)e.Records)),
    ];

    /// <summary>SCSKiller's dictionary entries its writing takes out of the leaf: in its file of the warm's alone written anew,
    /// and among its entries in Steam's.</summary>
    static List<(Out O, Record R)> Going(List<Out> outs) =>
    [
        .. outs.Where(o => o.Existing.Bin != null)
            .SelectMany(o => (o.Write && !o.Target.SteamFirst && o.Ours ? o.Existing.Records : o.Target.SteamFirst && !o.Kept ? o.Mine : []).Where(r => r.IsDictionary).Select(r => (o, r))),
    ];

    /// <summary>The id of the dictionary entry <paramref name="r"/>; null when it doesn't hold one.</summary>
    static uint? DictIdAt(SafeFileHandle bin, Record r)
    {
        var head = new byte[GlCacheFile.EntryHeaderSize + 8];
        return r.Length >= head.Length && RandomAccess.Read(bin, head, r.Offset) == head.Length ? GlCacheFile.DictionaryId(head.AsSpan(GlCacheFile.EntryHeaderSize)) : null;
    }

    /// <summary>An entry of <paramref name="sources"/> but a dictionary is encoded with dictionary <paramref name="id"/>,
    /// read from its frame's header.</summary>
    static bool Framed(IEnumerable<(SafeFileHandle Bin, IEnumerable<Record> Records)> sources, uint id)
    {
        var start = new byte[GlCacheFile.EntryHeaderSize + 22];   // the entry's header, the u32 size and the longest frame header
        foreach (var (bin, records) in sources)
            foreach (var r in records.Where(r => !r.IsDictionary))
            {
                var n = RandomAccess.Read(bin, start.AsSpan(0, (int)Math.Min(start.Length, r.Length)), r.Offset);
                if (n > GlCacheFile.EntryHeaderSize && GlCacheFile.FrameDictId(start.AsSpan(GlCacheFile.EntryHeaderSize, n - GlCacheFile.EntryHeaderSize)) == id) return true;
            }
        return false;
    }

    /// <summary>Keeps in the leaf's read-only files every dictionary SCSKiller recorded for it (<paramref name="recorded"/>)
    /// that they hold, a copy whole and checking (<see cref="Permanent"/>). One no file would keep goes after the entries of
    /// Steam's merged file (<see cref="Out.Carried"/>), copied by <see cref="WriteTemp"/>. The driver reads the same recorded
    /// one first: a placement never makes another the leaf's, and on <paramref name="removal"/> another that comes first
    /// once SCSKiller's entries are gone is refused while the game's entries in the leaf use the one that did
    /// (<see cref="Framed"/>). Refused, everything left as it is, when one can't be kept.</summary>
    void KeepRecorded(LeafFiles files, List<Out> outs, bool removal, HashSet<string> recorded)
    {
        var copies = new List<(Out O, Record R, uint Id, string Hash)>();
        foreach (var o in outs.Where(o => o.Existing.Bin != null))
            foreach (var r in o.Existing.Records.Where(r => r.IsDictionary))
                if (RecordedCopy(o.Existing.Bin!, r, recorded) is { } c) copies.Add((o, r, c.Id, c.Hash));
        var lines = new List<string>();
        foreach (var (o, r, id, hash) in copies)
        {
            if (Dictionaries(outs, recorded, written: true).Any(a => a.Hash == hash)) continue;
            var merged = outs.FirstOrDefault(x => x.Target.SteamFirst && !x.Kept && x.Existing is { Exists: true, Broken: null, Bin: not null });
            if (merged == null || !merged.Write && merged.Existing.Sums(out _) == null)
                throw new Refused(SteamPlaceState.Failed, $"SCSKiller's dictionary {id:x8} has no file of Steam's to stay in: left as it is");
            if (!merged.Write)
            {
                merged.Steam.Clear();
                merged.Steam.AddRange(merged.Existing.Records);
                (merged.End, merged.Write) = (GlCacheFile.HeaderSize + merged.SteamBytes, true);
            }
            merged.Carried.Add((o.Existing.Bin!, r));
            merged.End += r.Length;
            lines.Add($"{merged.Target.Name}: SCSKiller's dictionary {id:x8} goes there, the leaf keeps it");
        }
        if (Dictionaries(outs, recorded, written: false).FirstOrDefault() is { Hash: { } first, Id: { } was }
            && Dictionaries(outs, recorded, written: true).FirstOrDefault() is var now && now.Hash != first)
        {
            if (!removal) throw new Refused(SteamPlaceState.Failed, $"SCSKiller's dictionary {was:x8} would stop being the leaf's: left as it is");
            if (Framed(Staying(outs, files.Files), was))
                throw new Refused(SteamPlaceState.Failed, now.Id == was
                    ? $"the game's entries in Steam's folder use SCSKiller's dictionary {was:x8}, and the copy of it that stays in {now.O.Target.Name} isn't the one SCSKiller recorded: left as it is"
                    : $"the game's entries in Steam's folder use SCSKiller's dictionary {was:x8}, which can't stay there: left as it is");
        }
        foreach (var line in lines) Log?.Invoke(line);
    }

    /// <summary>A placement's own file of the warm's alone that holds a dictionary SCSKiller recorded keeps it first, its
    /// entries stored after it: the dictionary the driver reads stays the one it reads. The game's slot keeps none
    /// (<see cref="Sink.NeedsDict"/>).</summary>
    static void KeepFirst(Out o, HashSet<string> recorded)
    {
        if (o.Target.SteamFirst || o.Target.Slot || !o.Ours || o.Existing.Bin is not { } bin) return;
        if (o.Existing.Records.FirstOrDefault(r => r.IsDictionary && RecordedCopy(bin, r, recorded) != null) is { Key: not null } r)
        {
            o.Carried.Add((bin, r));
            o.End += r.Length;
        }
    }

    /// <summary>The id and hash of the dictionary entry <paramref name="r"/> when it is one SCSKiller recorded
    /// (<see cref="IsRecorded"/>).</summary>
    static (uint Id, string Hash)? RecordedCopy(SafeFileHandle bin, Record r, HashSet<string> recorded) =>
        r.Length <= MaxEntry && Whole(bin, r) is { } entry && GlCacheFile.Checks(entry, r)
        && Convert.ToHexString(SHA256.HashData(entry.AsSpan(GlCacheFile.EntryHeaderSize))) is var hash && recorded.Contains(hash)
        && GlCacheFile.DictionaryId(entry.AsSpan(GlCacheFile.EntryHeaderSize)) is { } id ? (id, hash) : null;

    /// <summary>The SHA-256 of each dictionary SCSKiller recorded for the leaf: its manifest's and the one it placed before,
    /// its owner note's, and each kept in the game's data folder for the leaf.</summary>
    HashSet<string> RecordedHashes(Manifest? m, OwnerNote? note, string target)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        if (m != null && m.Target == target)
            foreach (var d in new[] { m.Dictionary, m.WasDictionary }.OfType<DictPlaced>()) hashes.Add(d.Hash);
        if (note != null && Owns(note))
            foreach (var h in new[] { note.DictionaryHash, note.WasDictionaryHash }.OfType<string>()) hashes.Add(h);
        foreach (var (_, hash) in KeptDictionaries(LeafOf(target))) hashes.Add(hash);
        return hashes;
    }

    /// <summary>The ids of the dictionaries SCSKiller recorded for the leaf (<see cref="RecordedHashes"/>).</summary>
    HashSet<uint> RecordedIds(Manifest? m, OwnerNote? note, string target)
    {
        var ids = new HashSet<uint>();
        if (m != null && m.Target == target)
            foreach (var d in new[] { m.Dictionary, m.WasDictionary }.OfType<DictPlaced>()) ids.Add(d.Id);
        if (note != null && Owns(note))
            foreach (var id in new[] { note.DictionaryId, note.WasDictionaryId }.OfType<uint>()) ids.Add(id);
        foreach (var (entry, _) in KeptDictionaries(LeafOf(target)))
            if (GlCacheFile.DictionaryId(entry.AsSpan(GlCacheFile.EntryHeaderSize)) is { } id) ids.Add(id);
        return ids;
    }

    /// <summary>The dictionary entry <paramref name="r"/> is whole, checks (MurmurHash3) and is one SCSKiller recorded
    /// (<see cref="RecordedHashes"/>): Steam's merge checks an entry's header alone.</summary>
    static bool IsRecorded(SafeFileHandle bin, Record r, HashSet<string> recorded) =>
        r.Length <= MaxEntry && Whole(bin, r) is { } entry && GlCacheFile.Checks(entry, r)
        && recorded.Contains(Convert.ToHexString(SHA256.HashData(entry.AsSpan(GlCacheFile.EntryHeaderSize))));

    /// <summary>The dictionary <paramref name="d"/>, kept in the game's data folder; null when the file is gone. Refused when
    /// it isn't the one recorded.</summary>
    LeafDict? StoredDictionary(DictPlaced? d, string leaf, byte[] key)
    {
        if (d == null) return null;
        var path = DictFile(leaf, d.Hash);
        if (!File.Exists(path)) return null;
        var kept = IsFile(path) && new FileInfo(path).Length is > GlCacheFile.EntryHeaderSize and <= MaxEntry ? AsDictionary(File.ReadAllBytes(path), key, FromKept) : null;
        if (kept == null || kept.Hash != d.Hash || kept.Sum != d.Sum || kept.Size != d.Size)
            throw new Refused(SteamPlaceState.Failed, $"{Path.GetFileName(path)} isn't the dictionary SCSKiller recorded for the leaf");
        return kept;
    }

    /// <summary>The dictionary the manifest names, read back from SCSKiller's files in the leaf. Refused when one of them holds
    /// another.</summary>
    static LeafDict? PlacedDictionary(Manifest? m, Folder dir, List<Out> outs, byte[] key)
    {
        if (m is not { Pending: false, Dictionary: { } d } || m.Target != dir.Path) return null;
        LeafDict? found = null;
        foreach (var o in outs.Where(o => !o.Kept && o.Existing.Bin != null))
            foreach (var r in (o.Target.SteamFirst ? o.Mine : o.Ours ? o.Existing.Records : []).Where(r => r.IsDictionary))
            {
                var placed = r.Length <= MaxEntry && Whole(o.Existing.Bin!, r) is { } e ? AsDictionary(e, key, FromKept) : null;
                if (placed == null || placed.Hash != d.Hash || placed.Sum != d.Sum)
                    throw new Refused(SteamPlaceState.Failed, $"SCSKiller's dictionary entry in {o.Target.Name} isn't the one it recorded");
                found ??= placed;
            }
        return found;
    }

    static LeafDict? AsDictionary(byte[] entry, byte[] key, string source) =>
        entry.Length > GlCacheFile.EntryHeaderSize && GlCacheFile.Checks(entry, new Record(key, 0, (uint)(entry.Length - GlCacheFile.EntryHeaderSize)))
        && GlCacheFile.DictionaryId(entry.AsSpan(GlCacheFile.EntryHeaderSize)) is { } id ? new(entry, id, source) : null;

    /// <summary>A dictionary trained from about <see cref="DictSamples"/> of the warm's entries, spread evenly.</summary>
    LeafDict Train(byte[] key, WarmSets sets, List<(int Set, Record Record)> warm, Lowered lowered, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var n = Math.Min(DictSamples, warm.Count);
        var (dict, used) = lowered.Run(() =>
        {
            var samples = new List<byte[]>(n);
            using (var plain = new Decompressor())
                for (var i = 0; i < n; i++)
                {
                    if (i % 256 == 0) ct.ThrowIfCancellationRequested();
                    var (set, r) = warm[(int)((long)i * warm.Count / n)];
                    // one that doesn't check or decode is skipped here as the placement skips it
                    if (Whole(sets.All[set].Bin, r) is not { } stored || !GlCacheFile.Checks(stored, r) || stored.Length < GlCacheFile.EntryHeaderSize + 4) continue;
                    var payload = stored.AsSpan(GlCacheFile.EntryHeaderSize);
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    if (size > int.MaxValue / 2) continue;
                    var sample = new byte[size];
                    try
                    {
                        if (plain.Unwrap(payload[4..], sample) == size) samples.Add(sample);
                    }
                    catch (ZstdException) { }
                }
            try { return (GlCacheDict.Train(samples, DictCapacity, Level, lowered.MaximumConcurrencyLevel), samples.Count); }
            catch (InvalidDataException e) { throw new NoDictionary(e.Message); }
        }, ct);
        Log?.Invoke($"trained a dictionary from {used:N0} of the warm's entries in {clock.Elapsed.TotalSeconds:0.0} s");
        return new(GlCacheFile.Entry(key, dict), GlCacheFile.DictionaryId(dict) ?? throw new NoDictionary("the trained dictionary isn't a zstd dictionary"), FromTrained);
    }

    /// <summary>Keeps <paramref name="dict"/> in the game's data folder before any manifest names it.</summary>
    void SaveDictionary(string leaf, LeafDict dict)
    {
        var path = DictFile(leaf, dict.Hash);
        if (!File.Exists(path) && !Directory.Exists(path)) AppStore.WriteAtomic(path, dict.Entry, ownerOnly: true);
        else if (!IsFile(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(dict.Entry))
            throw new Refused(SteamPlaceState.Failed, $"{Path.GetFileName(path)} isn't the dictionary its name says");
    }

    /// <summary>Deletes the dictionaries kept in the game's data folder but <paramref name="keep"/>: called once the manifest
    /// names no other.</summary>
    void ForgetDictionaries(params string[] keep)
    {
        if (!Directory.Exists(gameDir)) return;
        foreach (var f in Directory.EnumerateFiles(gameDir, "steam-place.*.dict").Where(f => !keep.Contains(f))) File.Delete(f);
    }

    /// <summary>The coding of SCSKiller's entries still holds: under the leaf's dictionary, every file the manifest lists
    /// holds the same one; stored for want of one, Steam's files still hold what they held. Reads the files through
    /// <see cref="Existing"/> in the leaf <see cref="UpToDate"/> opened, and they must be the ones read when it returns.</summary>
    bool DictionaryUpToDate(Manifest m, Folder dir)
    {
        if (Coding != SteamEntryCoding.LeafDictionary || m.Coded == SteamEntryCoding.LeafDictionary && m.Dictionary == null) return true;
        if (m.Coded != SteamEntryCoding.LeafDictionary && m.NoDictionary == null) return false;
        var files = new List<(FileEntry File, Existing Now)>();
        try
        {
            // stored: the merged file read as the placement read it when it took Missing
            foreach (var t in Targets)
                if (m.Files.FirstOrDefault(f => f.Name == t.Name) is { } f)
                    files.Add((f, Existing.Read(dir.At, f.Name, walk: f.SteamFirst && m.Coded != SteamEntryCoding.LeafDictionary)));
            var same = m.Coded == SteamEntryCoding.LeafDictionary ? SameDictionary(m.Dictionary!, files)
                : m.NoDictionary == Missing(files.Where(x => x.File.SteamFirst || x.File.SteamOwns)
                    .Select(x => (x.File.Name, x.Now, x.File.SteamFirst && x.File.Keys > 0 ? ReadOurs(x.File.Name) : [])));
            return same && files.All(x => x.Now.Unchanged(dir.At, x.File.Name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            foreach (var x in files) x.Now.Dispose();
        }
    }

    /// <summary>What the leaf's dictionary depends on in Steam's <paramref name="files"/>: each one's header bytes 8 to 16 and
    /// its first dictionary entry that isn't SCSKiller's, or for a file that can't be copied, its .toc's hash and its .bin's
    /// length. Null when one can't be read.</summary>
    static string? Missing(IEnumerable<(string Name, Existing File, HashSet<Placed> Ours)> files)
    {
        var parts = new List<string>();
        var head = new byte[GlCacheFile.EntryHeaderSize];
        foreach (var (name, e, ours) in files)
        {
            if (!e.Exists) parts.Add($"{name}:none");
            else if (e.Broken != null || e.Bin is not { } bin) parts.Add($"{name}:{e.TocHash}:{e.BinLength}");
            else
            {
                var part = $"{name}:{Convert.ToHexString(e.BinHeader, 8, 8)}:-";
                foreach (var r in e.Records.Where(r => r.IsDictionary))
                {
                    if (RandomAccess.Read(bin, head, r.Offset) != head.Length) return null;
                    var sum = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(20));
                    if (ours.Contains(new(Key.Of(r.Key), sum))) continue;
                    part = $"{part[..^1]}{Convert.ToHexString(r.Key)}:{r.Size}:{sum:x16}";
                    break;
                }
                parts.Add(part);
            }
        }
        return string.Join(';', parts);
    }

    /// <summary>Each dictionary entry of the leaf's <paramref name="files"/> is <paramref name="d"/>, whole and checking, and
    /// each file the placement left holding it still does. False when one of Steam's is another, refused when one of
    /// SCSKiller's is.</summary>
    bool SameDictionary(DictPlaced d, List<(FileEntry File, Existing Now)> files)
    {
        foreach (var (f, e) in files)
        {
            if (!e.Exists)
            {
                if (f.Dictionary) return false;
                continue;
            }
            if (!e.TocRead || e.Bin is not { } bin) return false;
            var own = !f.SteamFirst && !f.SteamOwns;   // SCSKiller's file of the warm's alone
            var mine = f.SteamFirst && f.Keys > 0 ? ReadOurs(f.Name) : [];
            var held = false;
            foreach (var r in e.Records.Where(x => x.IsDictionary))
            {
                var entry = r.Length <= MaxEntry ? Whole(bin, r) : null;
                var sum = entry != null ? BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(20)) : 0;
                if (entry != null && GlCacheFile.Checks(entry, r) && r.Key.AsSpan().SequenceEqual(DictKey(e.BinHeader)) && r.Size == d.Size && sum == d.Sum
                    && Convert.ToHexString(SHA256.HashData(entry.AsSpan(GlCacheFile.EntryHeaderSize))) == d.Hash)
                    held = true;
                else if (own || mine.Contains(new(Key.Of(r.Key), sum))) throw new Refused(SteamPlaceState.Failed, $"SCSKiller's dictionary entry in {f.Name} isn't the one it recorded");
                else return false;
            }
            if (f.Dictionary && !held) return false;
        }
        return true;
    }

    /// <summary>Leaves SCSKiller's dictionary entries among Steam's in <paramref name="name"/>, each one it recorded
    /// (<paramref name="recorded"/>): the leaf keeps them. Another goes, and <see cref="KeepRecorded"/> puts a copy that is
    /// one in its place or refuses.</summary>
    void KeepDictionary(string name, Existing e, bool[] mine, HashSet<string> recorded)
    {
        for (var i = 0; i < mine.Length; i++)
            if (mine[i] && e.Records[i].IsDictionary && e.Bin is { } bin && IsRecorded(bin, e.Records[i], recorded))
            {
                mine[i] = false;
                Log?.Invoke($"{name}: SCSKiller's dictionary stays there, the leaf keeps it");
            }
    }

    /// <summary>Threads at <see cref="Priority"/> for the encode and the training. Linux keeps a nice and an I/O priority per
    /// thread, a thread starts with its maker's, and an unprivileged one can't raise them back: these threads are the
    /// placement's own and end with it.</summary>
    sealed class Lowered : TaskScheduler, IDisposable
    {
        [ThreadStatic] static Lowered? _owner;
        readonly BlockingCollection<Task> _tasks = [];
        readonly List<Thread> _threads = [];

        public Lowered(int threads, WarmPriority priority)
        {
            var idle = priority == WarmPriority.Idle;
            for (var i = 0; i < threads; i++)
            {
                var t = new Thread(() =>
                {
                    Libc.LowerThreadPriority(idle ? ProtonLaunch.IdleNice : ProtonLaunch.BelowNice);
                    Libc.SetThreadIoPriority(idle ? Libc.IoPriorityIdle : Libc.IoPriorityLowest);
                    _owner = this;
                    foreach (var task in _tasks.GetConsumingEnumerable()) TryExecuteTask(task);
                }) { IsBackground = true, Name = "SCSKiller encode" };
                t.Start();
                _threads.Add(t);
            }
        }

        /// <summary><paramref name="f"/> on one of the threads, its exception as it threw it.</summary>
        public T Run<T>(Func<T> f, CancellationToken ct) => Task.Factory.StartNew(f, ct, TaskCreationOptions.DenyChildAttach, this).GetAwaiter().GetResult();

        public override int MaximumConcurrencyLevel => _threads.Count;
        protected override void QueueTask(Task task) => _tasks.Add(task);
        protected override bool TryExecuteTaskInline(Task task, bool queued) => _owner == this && TryExecuteTask(task);
        protected override IEnumerable<Task> GetScheduledTasks() => [.. _tasks];

        public void Dispose()
        {
            _tasks.CompleteAdding();
            foreach (var t in _threads) t.Join();
            _tasks.Dispose();
        }
    }
}
