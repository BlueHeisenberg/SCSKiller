using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.App;

/// <summary>A game's recording: SCSKiller's copy (games\&lt;id&gt;\recording.db, compact: <see cref="PsoDb.WriteCompact"/>)
/// is the durable one, and the recorder's scskiller.db in the game folder is an inbox, emptied once imported. The recorder
/// skips what the copy already has, and the bytes of the shaders the game ships, through <see cref="KeysFile"/>, next to
/// its scskiller.ini.</summary>
public static class Recordings
{
    public const string KeysFile = "scskiller.keys";
    public const string WarmedFile = "scskiller.warmed";

    /// <summary>The files the app writes next to the recorder, with the temp names their writes go through.</summary>
    public static readonly string[] AppFiles = [KeysFile, WarmedFile, KeysFile + ".tmp", WarmedFile + ".tmp"];

    /// <summary>Written by each warm process next to its log (proxy.cpp warm_main): the record keys ('P' records' for plan
    /// items) of the items it didn't create: failed, abandoned, or skipped for having removed the device before.</summary>
    public const string FailedFile = "scskiller_failed.keys";

    // proxy.cpp load_warmed: magic, u32 n, n record keys, then tuple hashes
    static ReadOnlySpan<byte> WarmedMagic => "SCSKWRM1"u8;

    /// <summary>The <see cref="WarmedFile"/> of a compile's work folder dbs (the warm's scskiller.db and scskiller_gen.db):
    /// the sorted keys of the pipelines and state objects it created, then the sorted (stages + root signature) tuple hashes
    /// of every pipeline it created, a plan item's from its template with the item's shaders and root signature, as the
    /// proxy's rec_tuple does. What the materialize skipped isn't in the dbs; an item one of <paramref name="failed"/>
    /// (<see cref="FailedFile"/>s) names, by its record's key, wasn't created and counts for nothing. <paramref name="previous"/>:
    /// an earlier warm's file whose keys and tuples still hold (a warm that left out what it created, CompiledItems).
    /// <paramref name="created"/>: keys a warm created that no db holds (a Vulkan warm's, VulkanPlan.Created), no tuples.</summary>
    public static byte[] Warmed(IEnumerable<string> dbs, IEnumerable<string> failed, byte[]? previous = null, IEnumerable<string>? created = null)
    {
        var notCreated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in failed.Where(File.Exists))
            foreach (var k in File.ReadAllBytes(f).Chunk(20).Where(c => c.Length == 20)) notCreated.Add(Convert.ToHexStringLower(k));
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        var tuples = new SortedSet<string>(StringComparer.Ordinal);
        var templates = new Dictionary<string, Rec>();
        var items = new List<byte[]>();
        if (previous is { Length: >= 12 } p0 && p0.AsSpan(0, 8).SequenceEqual(WarmedMagic) && BitConverter.ToUInt32(p0, 8) is var pn && 12 + 20L * pn <= p0.Length)
        {
            var hashes = p0[12..].Chunk(20).Where(c => c.Length == 20).Select(Convert.ToHexStringLower).ToList();
            keys.UnionWith(hashes.Take((int)pn));
            tuples.UnionWith(hashes.Skip((int)pn));
        }
        keys.UnionWith(created ?? []);
        foreach (var db in dbs.Where(File.Exists))
            foreach (var r in Read(db))
            {
                if (r.Tag is 'G' or 'C' or 'S') templates[r.Key] = r;   // a plan item's template even when it failed itself
                if (notCreated.Contains(r.Key)) continue;
                if (r.Tag is 'G' or 'C' or 'S')
                {
                    keys.Add(r.Key);
                    if (TryParse(r) is { } p) tuples.Add(TupleHash(p.Rs, p.Stages));
                }
                else if (IsStateObject(r.Tag)) keys.Add(r.Key);
                else if (r.Tag == 'P') items.Add(r.Payload);
            }
        foreach (var item in items)
        {
            var (template, rs, stages) = ParseItem(item);
            if (!templates.TryGetValue(template, out var t) || TryParse(t) is not { } p) continue;   // the proxy skips it too
            var merged = new SortedDictionary<int, string>(p.Stages);
            foreach (var (s, h) in stages)
                if (h != Zero && (merged.ContainsKey(s) || t.Tag == 'G' && s is >= 1 and <= 5)) merged[s] = h;
            tuples.Add(TupleHash(rs != Zero ? rs : p.Rs, merged));
        }
        return WarmedBytes(keys, tuples);

        static Pso? TryParse(Rec r)
        {
            try { return Parse(r); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException or OverflowException) { return null; }
        }
    }

    static byte[] WarmedBytes(SortedSet<string> keys, SortedSet<string> tuples)
    {
        using var m = new MemoryStream();
        m.Write(WarmedMagic);
        m.Write(BitConverter.GetBytes((uint)keys.Count));
        foreach (var k in keys.Concat(tuples)) m.Write(Convert.FromHexString(k));
        return m.ToArray();
    }

    /// <summary>One <see cref="WarmedFile"/> naming what any of <paramref name="files"/> names (a malformed one: nothing).</summary>
    public static byte[] UnionWarmed(IEnumerable<string> files)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        var tuples = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var b = File.ReadAllBytes(f);
            if (b.Length < 12 || !b.AsSpan(0, 8).SequenceEqual(WarmedMagic) || (b.Length - 12) % 20 != 0 || BitConverter.ToUInt32(b, 8) > (b.Length - 12) / 20) continue;
            var n = BitConverter.ToUInt32(b, 8);
            for (var i = 0; 12 + 20 * i < b.Length; i++) (i < n ? keys : tuples).Add(Hex(b.AsSpan(12 + 20 * i, 20)));
        }
        return WarmedBytes(keys, tuples);
    }

    /// <summary>proxy.cpp tuple_hash: SHA-1 of the root signature hash, then per stage (by subobject type) its type byte and
    /// shader hash.</summary>
    public static string TupleHash(string rs, IReadOnlyDictionary<int, string> stages)
    {
        using var m = new MemoryStream();
        m.Write(Convert.FromHexString(rs));
        foreach (var (s, h) in stages.OrderBy(x => x.Key).ThenBy(x => x.Value, StringComparer.Ordinal))
        {
            m.WriteByte((byte)s);
            m.Write(Convert.FromHexString(h));
        }
        return Hex(System.Security.Cryptography.SHA1.HashData(m.ToArray()));
    }

    /// <summary>Held around a read-modify-write of <paramref name="store"/> (import, compaction, migration, clearing) by every
    /// process: two writers would each replace the file with their own merge and drop the other's records. Every writer
    /// of <paramref name="store"/> holds it, so a temp file found under it belongs to a writer that is gone: removed once
    /// not written for an hour, or written before the PC started (no process is looked at). <paramref name="wait"/>,
    /// <paramref name="ct"/>: as <see cref="AppStore.PathGate"/>.</summary>
    public static IDisposable Lock(string store, TimeSpan? wait = null, CancellationToken ct = default)
    {
        var gate = new AppStore.PathGate(store, wait, ct);
        var dir = Path.GetDirectoryName(Path.GetFullPath(store))!;
        var booted = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        try
        {
            if (Directory.Exists(dir))
                foreach (var tmp in new DirectoryInfo(dir).EnumerateFiles(Path.GetFileName(store) + ".*tmp"))
                    try
                    {
                        if (tmp.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1) || tmp.LastWriteTimeUtc < booted) tmp.Delete();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // still open: left
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // left for the next writer
        return gate;
    }

    /// <summary>Tests: runs once a keys file is written, before it replaces the old one.</summary>
    internal static Action? BeforeKeysPublished;

    // proxy.cpp load_keys: magic, then 20-byte record keys and blob hashes
    static ReadOnlySpan<byte> KeysMagic => "SCSKKEY1"u8;

    /// <summary>Writes <paramref name="store"/> (none: empty) plus the records of <paramref name="inbox"/> it lacks, in that
    /// order, as a compact recording at <paramref name="store"/>. A shader blob that a record names and <paramref name="shipped"/>
    /// has is left out: the install gives it back (<see cref="Rehydrate"/>); root signatures and every other blob stay. Returns
    /// the keys of the pipeline and state object records it added.</summary>
    public static HashSet<string> Merge(string store, string? inbox, Func<string, bool>? shipped) => Merge(store, inbox, shipped, out _);

    /// <param name="nvAdded">an 'N' record the store lacked was added: the plan may take another NVAPI state for the PSOs it
    /// synthesizes (<see cref="PlanBuilder.RasterNv"/>)</param>
    public static HashSet<string> Merge(string store, string? inbox, Func<string, bool>? shipped, out bool nvAdded)
    {
        var hasStore = File.Exists(store);
        var hasInbox = inbox != null && File.Exists(inbox);
        var nv = false;
        IEnumerable<Rec> Sources() => (hasStore ? Read(store) : []).Concat(hasInbox ? Read(inbox!) : []);
        var named = shipped == null ? [] : Rehydrate.References(Sources().Where(r => r.Tag != 'B'));
        var seen = new HashSet<string>();
        var added = new HashSet<string>();
        IEnumerable<Rec> Union()
        {
            if (hasStore) foreach (var r in Read(store)) if (seen.Add(Id(r))) yield return r;
            if (hasInbox)
                foreach (var r in Read(inbox!))
                    if (Id(r) is var id && seen.Add(id))
                    {
                        if (r.Tag == 'N') nv = true;
                        else if (r.Tag is not ('B' or 'W')) added.Add(id);   // its key: one string for both sets
                        yield return r;
                    }
        }
        bool Dropped(Rec r) => r.Tag == 'B' && shipped != null && Hex(r.Payload.AsSpan(0, 20)) is var h && named.Contains(h) && shipped(h)
                               && !Dxbc.IsRootSignatureOnly(r.Payload.AsSpan(20));
        WriteCompact(store, Union().Where(r => !Dropped(r)));
        KeyFiles.Forget(store);
        nvAdded = nv;
        return added;
    }

    // a blob by its hash: cheaper than hashing its bytes again
    static string Id(Rec r) => r.Tag == 'B' ? "B" + Hex(r.Payload.AsSpan(0, 20)) : r.Key;

    /// <summary>Writes <paramref name="path"/>: every shader of <paramref name="shipped"/> (the recorder then names it by
    /// hash, without its bytes: the install gives them back), every blob of <paramref name="store"/> and the key of every
    /// record it holds that replays from the two. A record naming a blob neither has is left out, so the recorder records it
    /// again, with its bytes, if the game still creates it. Returns how many were left out. With nothing to name, no file.
    /// Not <paramref name="nameShipped"/> (the install may be another build than <paramref name="shipped"/>'s): its shaders
    /// only say which records stay named, and the recorder records every new shader with its bytes. Nothing created,
    /// deleted or published unless <paramref name="publish"/> holds before it starts and right before the publish (the
    /// proxy is still there and the game isn't running: a rollback deletes the file without the recording lock).</summary>
    public static int WriteKeys(string store, IReadOnlySet<string>? shipped, string path, bool nameShipped = true, Func<bool>? publish = null)
    {
        var blobs = new HashSet<string>(nameShipped && shipped != null ? shipped : []);
        var stored = File.Exists(store);
        if (stored)
            foreach (var r in Read(store))
                if (r.Tag == 'B') blobs.Add(Hex(r.Payload.AsSpan(0, 20)));
        // read again, one record at a time: 1.5 million records held at once took 1.3 GB
        IEnumerable<Rec> Records() => stored ? Read(store).Where(r => r.Tag != 'B') : [];
        bool Kept(Rec r) => r.Tag == 'N' || Rehydrate.References([r]).All(h => blobs.Contains(h) || shipped?.Contains(h) == true);
        if (publish?.Invoke() == false) return Records().Count(r => !Kept(r));   // nothing created or deleted
        var (kept, left) = (0, 0);
        var (file, tmp) = ScsKiller.CreateTemp(path, ".tmp");
        try
        {
            using (var f = new BufferedStream(file, 1 << 16))
            {
                f.Write(KeysMagic);
                foreach (var h in blobs) f.Write(Convert.FromHexString(h));
                foreach (var r in Records())
                    if (!Kept(r)) left++;
                    else
                    {
                        f.Write(Convert.FromHexString(r.Key));
                        kept++;
                    }
            }
            if (blobs.Count + kept == 0)
            {
                if (publish?.Invoke() != false) File.Delete(path);
            }
            else
            {
                BeforeKeysPublished?.Invoke();
                if (publish?.Invoke() != false)
                {
                    ScsKiller.RefuseLink(path);
                    File.Move(tmp, path, true);
                }
            }
        }
        finally { File.Delete(tmp); }
        return left;
    }

    /// <summary>The records of <paramref name="store"/> a layer wrapping the device (a mod) made, by its 'W' records: the
    /// game's creates it changed before the driver got them, and its own creates. (0, 0): recorded without one.</summary>
    public static (int Changed, int Own) Layered(string store)
    {
        var pairs = Pairs(store);
        var own = pairs.Count(p => p.EndsWith(Zero, StringComparison.Ordinal));
        return (pairs.Count - own, own);
    }

    /// <summary>The keys of the records of <paramref name="store"/> a layer made ('W': changed or its own); a store that
    /// can't be read throws: what is shared is checked against them.</summary>
    public static HashSet<string> LayerMade(string store) => [.. Pairs(store).Select(p => p[..40])];

    /// <summary>A recording whose 'W' records can't all be read yet: a torn tail (normal while the recorder writes its
    /// inbox) or a malformed 'W'. Nothing is shared until it reads whole, usually once the game has closed.</summary>
    public sealed class IncompleteLayerList(string message) : IOException(message);

    static IReadOnlySet<string> Pairs(string store) =>
        KeyFiles.Keys(store, p =>
        {
            // the recorder holds its inbox open for writing while the game runs: read alongside it
            using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[8];
            var n = f.ReadAtLeast(head, 8, false);
            f.Position = 0;
            // a proxy db starts with a record's tag, never 0: a leading 0 is a compact recording's magic, whole or damaged
            if (n > 0 && head[0] == 0 && !head.AsSpan(0, n).SequenceEqual("\0SCSKREC"u8)) throw new IncompleteLayerList($"{p}: a damaged compact header");
            List<Rec> pairs;   // only the 'W' records kept: a whole recording read at once took over 1 GB
            try { pairs = [.. (n > 0 && head[0] == 0 ? Read(p) : Whole(new BufferedStream(f, 1 << 20), f.Length, p)).Where(r => r.Tag == 'W')]; }
            catch (InvalidDataException e) { throw new IncompleteLayerList($"{p}: {e.Message}"); }
            if (pairs.Select(r => r.Payload.Length).FirstOrDefault(n => n != 40, 40) is not 40 and var bad)
                throw new IncompleteLayerList($"{p}: a 'W' record of {bad} bytes");
            return [.. pairs.Select(r => Hex(r.Payload))];
        }, "layer", failOpen: false);

    /// <summary>A proxy db's records, all of them: a torn tail throws, where <see cref="Read(Stream)"/> stops quietly.</summary>
    static IEnumerable<Rec> Whole(Stream f, long length, string path)
    {
        var head = new byte[5];
        long at = 0;
        for (int n; (n = f.ReadAtLeast(head, 5, false)) > 0;)
        {
            var len = BitConverter.ToUInt32(head, 1);
            if (n < 5 || len > length - at - 5) throw new IncompleteLayerList($"{path}: a torn tail at byte {at}");
            var body = new byte[len];
            f.ReadExactly(body);
            at += 5 + len;
            yield return new Rec((char)head[0], body);
        }
    }

    static readonly object DiskGate = new();
    static (string Fingerprint, HashSet<string> Keys)? onDisk;
    static readonly Dictionary<string, (string? Stamp, string[] Inboxes)> InboxesOf = [];

    /// <summary>What a layer made by every recording on this PC, read from disk (not from the scan's games, which a
    /// first scan publishes only once evaluated): every games\*\recording.db, and the recorder's inbox (scskiller.db)
    /// next to each recorded exe in state.json. Files are stamped by metadata and sampled contents; the set is rebuilt
    /// only when one of them changed. Anything there that can't be listed, stat'ed or read throws (a recording not whole
    /// yet: <see cref="IncompleteLayerList"/>): what is shared is checked against it.</summary>
    public static HashSet<string> LayerMadeOnDisk(string dataDir)
    {
        var games = new DirectoryInfo(Path.Combine(dataDir, "games"));
        List<DirectoryInfo> dirs;
        try { dirs = [.. games.EnumerateDirectories()]; }
        catch (DirectoryNotFoundException) { return []; }   // only a missing folder is none: Exists is false on an error too
        var sources = new List<string>();
        var fingerprint = new System.Text.StringBuilder();
        void Add(string path, long length, long written) { sources.Add(path); fingerprint.Append(path).Append('|').Append(length).Append('|').Append(written).Append('|').Append(KeyFiles.Stamp(path, strict: true)).Append('\n'); }
        foreach (var dir in dirs)
            foreach (var f in dir.EnumerateFiles())
                if (f.Name.Equals("recording.db", StringComparison.OrdinalIgnoreCase)) Add(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks);
                else if (f.Name.Equals("state.json", StringComparison.OrdinalIgnoreCase))
                {
                    fingerprint.Append(f.FullName).Append('|').Append(KeyFiles.Stamp(f.FullName, strict: true)).Append('\n');
                    foreach (var inbox in Inboxes(f))
                        if (Stat(inbox) is { } st) Add(inbox, st.Length, st.Written);
                }
        var fp = fingerprint.ToString();
        // unchanged stamps (KeyFiles.Keys' own): the files hold what was read, so their keys stand even if a read would now fail
        lock (DiskGate)
            if (onDisk is { } c && c.Fingerprint == fp) return [.. c.Keys];
        HashSet<string> keys = [];
        foreach (var s in sources) keys.UnionWith(LayerMade(s));
        lock (DiskGate) onDisk = (fp, keys);
        return [.. keys];
    }

    /// <summary>The recorder inboxes a game's state.json names (next to its recorded exe, and the two of a move), parsed
    /// again only when the file changed.</summary>
    static string[] Inboxes(FileInfo state)
    {
        var stamp = KeyFiles.Stamp(state.FullName, strict: true);
        lock (DiskGate)
            if (InboxesOf.TryGetValue(state.FullName, out var c) && c.Stamp == stamp) return c.Inboxes;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(state.FullName));
        string[] inboxes = [.. new[] { "RecorderExe", "RecorderMoveFrom", "RecorderMoveTo" }
            .Select(n => !doc.RootElement.TryGetProperty(n, out var exe) ? null : exe.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => exe.GetString(),
                System.Text.Json.JsonValueKind.Null => null,
                _ => throw new InvalidDataException($"{state.FullName}: {n} is a {exe.ValueKind}, not a path"),
            })
            .OfType<string>().Where(p => p.Length > 0).Select(p => Path.Combine(Path.GetDirectoryName(p)!, "scskiller.db"))];
        lock (DiskGate) InboxesOf[state.FullName] = (stamp, inboxes);
        return inboxes;
    }

    /// <summary>A file's size and write time from its handle; null only when it (or its folder) isn't there.</summary>
    static (long Length, long Written)? Stat(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return (RandomAccess.GetLength(h), File.GetLastWriteTimeUtc(h).Ticks);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    /// <summary>Empties the recorder's scskiller.db once imported, only while nothing has it open (the recorder holds it for
    /// the whole session) and only at the <paramref name="imported"/> length it was read at: what came after isn't imported.</summary>
    public static bool Rotate(string inbox, long imported)
    {
        try
        {
            using var h = Games.GameFiles.OpenUnlinked(inbox);   // a link planted at its name would empty what it points to
            if (RandomAccess.GetLength(h) != imported) return false;
            RandomAccess.SetLength(h, 0);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
