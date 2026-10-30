using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.App;
using Record = SCSKiller.Core.Vendors.GlCacheFile.Record;

namespace SCSKiller.Core.Vendors;

/// <summary>The game's own slot (<see cref="SteamTarget.Slot"/>): under Steam's environment the game's process writes
/// steamapp_shader_cache&lt;n&gt;, n the lowest index it can lock, reads a file placed there whole and appends to it. At the
/// game's exit Steam folds every such fragment into the merged file first, then the merged file's entries, the first copy of
/// each key kept, with no cap, and deletes the fragments. Past 4 GiB the merged file's .toc offsets wrap: the driver reads
/// nothing there and Steam's next merge drops it. So the slot is learned while the game runs (<see cref="LearnSlot"/>), and
/// once Steam's merge is done the placement reads the merged file by walking its .bin, keeps what isn't SCSKiller's in
/// order, and fills the merged file, then the slot, which leaves <see cref="SteamTarget.Headroom"/> under 4 GiB for the next
/// session after the next fold. A fragment that isn't the slot as SCSKiller wrote it is never written, renamed or deleted.</summary>
sealed partial class SteamCachePlace
{
    /// <summary>Of the steamapp_shader_cache&lt;n&gt; files of a folder (path and n, its .toc and .bin), the n of the one the
    /// game's own process has open, Proton's helpers left out; null, with why, when it holds none or several or that can't be
    /// told. Tests: a stand-in.</summary>
    public Func<IReadOnlyList<(string Path, int N)>, (int? N, string? Why)> GameSlot { get; init; } = _ => (null, "the game's processes aren't looked at");

    SteamTarget? SlotTarget => Targets.FirstOrDefault(t => t.Slot);

    static bool Keyed(SteamTarget t) => t.SteamFirst || t.Slot;

    static bool Keyed(FileEntry f) => f.SteamFirst || f.Slot;

    static readonly ulong DictTail = BinaryPrimitives.ReadUInt64LittleEndian("_NVDICT_"u8);

    /// <summary>What the game's own slot was learned to be while it ran, in the folder at <see cref="Target"/>.</summary>
    sealed record SlotSeen(string Target, int N);

    /// <summary>The n of the slot learned in the manifest's folder; null while none is.</summary>
    int? SlotOf(Manifest m) => SlotTarget != null && m.Slot is { } s && s.Target == m.Target ? s.N : null;

    string SlotName(int n) => SlotTarget!.Name + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The n of a file set named like the slot; null for another name.</summary>
    int? SlotNumber(string name) => SlotTarget is { } t && name.StartsWith(t.Name, StringComparison.Ordinal) && name.Length is var l && l > t.Name.Length && l <= t.Name.Length + 3
        && int.TryParse(name.AsSpan(t.Name.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && SlotName(n) == name ? n : null;

    /// <summary>The steamapp_shader_cache&lt;n&gt; files of <paramref name="dir"/>, each .toc and .bin.</summary>
    List<(string Path, int N)> SlotFiles(string dir) =>
        !Directory.Exists(dir) ? [] : [.. from f in Directory.EnumerateFiles(dir) let name = Path.GetFileName(f) where name.EndsWith(".toc", StringComparison.Ordinal) || name.EndsWith(".bin", StringComparison.Ordinal)
            let n = SlotNumber(name[..^4]) where n != null orderby name select (Path.Combine(dir, name), n.Value)];

    /// <summary>The files a placement fills before the slot, in order, the merged file at its limit for a learned slot.</summary>
    IEnumerable<SteamTarget> Filled(int? slot) => Targets.Where(t => !t.Slot).Select(t => slot != null && t.WithSlot > 0 ? t with { Limit = t.WithSlot } : t);

    /// <summary>While the game runs: the steamapp_shader_cache&lt;n&gt; of the placement's folder that the game's own process
    /// holds (<see cref="GameSlot"/>), kept in the manifest for the placements after its exit. A slot not found leaves the one
    /// learned before. Returns what it found or why it found none; null when nothing is placed or another process is at it.</summary>
    public string? LearnSlot()
    {
        if (SlotTarget == null) return null;
        AppStore.PathGate gate;
        try { gate = new AppStore.PathGate(ManifestFile, TimeSpan.Zero); }
        catch (IOException e) when (IoErrors.HeldElsewhere(e)) { return null; }
        using (gate)
            try
            {
                if (LoadManifest() is not { Target.Length: > 0 } m || !Directory.Exists(m.Target)) return null;
                var leaf = LeafOf(m.Target);
                var was = SlotOf(m);
                var (n, why) = GameSlot(SlotFiles(m.Target));
                if (n is not { } k) return $"{leaf}: the game's own cache file isn't known, {why}" + (was is { } w ? $", {SlotName(w)} stays the one" : "");
                if (k == was) return $"{leaf}: the game's own cache file is {SlotName(k)}";
                m.Slot = new(m.Target, k);
                SaveManifest(m);
                return $"{leaf}: the game's own cache file is {SlotName(k)}" + (was is { } o ? $", was {SlotName(o)}" : "");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return $"looking for the game's own cache file failed: {e.Message}"; }
    }

    /// <summary>Why the placement waits for Steam's merge: with a slot in play, a steamapp_shader_cache&lt;k&gt; in the folder
    /// that isn't one of SCSKiller's slots as it wrote it. Steam folds it into the merged file at the game's exit.</summary>
    string? Unmerged(Folder dir, Manifest m)
    {
        if (SlotOf(m) == null && !m.Files.Any(f => f.Slot)) return null;
        foreach (var name in SlotFiles(dir.At).Select(f => SlotName(f.N)).Distinct())
            if (m.Files.FirstOrDefault(f => f.Slot && f.Name == name) is not { } f || !Unchanged(f, dir.At)) return $"Steam hasn't merged {name} into its merged file yet";
        return null;
    }

    /// <summary>The slot's files are still the ones placed: their inodes, change times, lengths and print, or, while a
    /// placement is pending, the inodes, lengths and write times of the pair it read or the one it wrote
    /// (<see cref="FileEntry.Sides"/>), which a rename keeps, and their print.</summary>
    static bool Unchanged(FileEntry f, string dir)
    {
        using var e = Existing.Read(dir, f.Name);
        return SlotOurs(f, e);
    }

    /// <summary>The slot <paramref name="e"/> read is the one placed, as <see cref="Unchanged(FileEntry, string)"/> tells.
    /// One the owner note lists without its identity never is (<see cref="FileEntry.Unproven"/>), nor one without either.</summary>
    static bool SlotOurs(FileEntry f, Existing e) => !f.Unproven && f.Ours.Any(p => p.Matches(e))
        && (f.TocStat != null ? e.TocStat == f.TocStat && e.BinStat == f.BinStat && e.TocLength == f.Toc && e.BinLength == f.Bin
            : f.Sides?.Any(s => s.Toc == e.TocId && s.Bin == e.BinId) == true);

    /// <summary>The slot <paramref name="name"/> as SCSKiller's owner note records it: its prints and, once placed, its
    /// identity. Without that it can't be told from a copy: <see cref="FileEntry.Unproven"/>. Null when the note isn't
    /// SCSKiller's or doesn't list it.</summary>
    FileEntry? NoteSlot(OwnerNote? note, string name)
    {
        if (note == null || !Owns(note) || SlotNumber(name) == null || note.Files.GetValueOrDefault(name) is not { Count: > 0 } prints) return null;
        var id = note.Slots.GetValueOrDefault(name);
        return new() { Name = name, Slot = true, Ours = prints, TocStat = id?.TocStat, BinStat = id?.BinStat, Toc = id?.Toc ?? -1, Bin = id?.Bin ?? -1, Unproven = id == null };
    }

    /// <summary>Without a manifest of the folder: the slots SCSKiller's owner note lists that are still as it wrote them, by
    /// their prints and identities, carried with their entries, all SCSKiller's, read from them. While one it lists is
    /// there otherwise, the placement waits: the game added to it, or it can't be told from a copy, and Steam folds it into
    /// the merged file at the game's exit.</summary>
    void NoteSlots(OwnerNote? note, Folder dir, List<FileEntry> carried)
    {
        foreach (var name in note?.Files.Keys.Order(StringComparer.Ordinal).ToList() ?? [])
        {
            if (NoteSlot(note, name) is not { } f) continue;
            using var e = Existing.Read(dir.At, name);
            if (!e.Exists) continue;
            string? bad = null;
            var sums = SlotOurs(f, e) && e.Broken == null ? e.Sums(out bad) : null;
            if (sums == null) throw new Refused(SteamPlaceState.Unmerged, $"Steam hasn't merged {name} into its merged file yet");
            f.Found = [.. e.Records.Select((r, i) => new Placed(Key.Of(r.Key), sums[i]))];
            (f.Keys, f.Dictionary) = (f.Found.Count, e.Records.Any(r => r.IsDictionary));
            carried.Add(f);
        }
    }

    /// <summary>The dictionaries the entries of the slots <paramref name="names"/> are encoded with, read from their frames:
    /// a slot holds none, the driver decodes it with the one of the read-only files.</summary>
    static HashSet<uint> SlotFramed(Folder dir, IEnumerable<string> names)
    {
        var ids = new HashSet<uint>();
        var start = new byte[GlCacheFile.EntryHeaderSize + 22];   // the entry's header, the u32 size and the longest frame header
        foreach (var name in names)
        {
            using var e = Existing.Read(dir.At, name);
            if (e is not { Broken: null, Bin: { } bin }) continue;
            foreach (var r in e.Records.Where(r => !r.IsDictionary))
            {
                var n = RandomAccess.Read(bin, start.AsSpan(0, (int)Math.Min(start.Length, r.Length)), r.Offset);
                if (n > GlCacheFile.EntryHeaderSize && GlCacheFile.FrameDictId(start.AsSpan(GlCacheFile.EntryHeaderSize, n - GlCacheFile.EntryHeaderSize)) is { } id and not 0)
                    ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>The SHA-256 of each dictionary in SCSKiller's own files of the leaf (<see cref="OurDictionaries"/>), whole and
    /// checking, whose id is one of <paramref name="ids"/> that no record has: its slots' entries are encoded with one it
    /// placed.</summary>
    static List<string> PlacedByFrames(List<Out> outs, HashSet<uint> ids, HashSet<uint> recordedIds)
    {
        var hashes = new List<string>();
        foreach (var (o, r) in OurDictionaries(outs))
            if (r.Length <= MaxEntry && Whole(o.Existing.Bin!, r) is { } entry && GlCacheFile.Checks(entry, r)
                && GlCacheFile.DictionaryId(entry.AsSpan(GlCacheFile.EntryHeaderSize)) is { } id && ids.Contains(id) && !recordedIds.Contains(id))
                hashes.Add(Convert.ToHexString(SHA256.HashData(entry.AsSpan(GlCacheFile.EntryHeaderSize))));
        return hashes;
    }

    /// <summary>The keys of a slot carried as it is: as listed, or as read from it (<see cref="NoteSlots"/>).</summary>
    HashSet<Placed> CarriedOurs(FileEntry f) => f.Found ?? ReadOurs(f.Name);

    /// <summary>SCSKiller's entries of its slots, which Steam's merge folds into the merged file.</summary>
    HashSet<Placed> FoldedOurs(Manifest m)
    {
        var ours = new HashSet<Placed>();
        foreach (var f in m.Files.Where(f => f.Slot && f.Keys > 0)) ours.UnionWith(ReadOurs(f.Name));
        return ours;
    }

    /// <summary>The learned slot as the placement fills it, last: up to its limit less its headroom less what the merged
    /// file keeps of Steam's and the game's, less the slots placed before that are still there as written, which stay as
    /// they are (<paramref name="carried"/>) and fold in front too; empty while a session wrote more than the headroom
    /// (<see cref="Manifest.PastHeadroom"/>). Null without a learned slot.</summary>
    Out? SlotOut(Manifest m, int? slot, Folder dir, List<Out> outs, OwnerNote? note, List<FileEntry> carried)
    {
        carried.AddRange(m.Files.Where(f => f.Slot && SlotNumber(f.Name) != slot && Unchanged(f, dir.At)));
        if (slot is not { } n || SlotTarget is not { } t) return null;
        var name = SlotName(n);
        var merged = outs.FirstOrDefault(o => o.Target.SteamFirst);
        var kept = merged == null ? 0 : merged.Kept ? Math.Max(merged.Existing.BinLength - GlCacheFile.HeaderSize, 0) : merged.SteamBytes;
        var limit = t.Limit - t.Headroom - kept - carried.Sum(f => f.Bin);
        // what the game wrote since the merged file was last read, as Steam's fold adds it there: past the headroom, the
        // driver's cap isn't the one measured. Unchanged, no session wrote, and left as it is, it holds SCSKiller's entries
        // too, which aren't told apart there: either keeps what was found
        if (merged is { Kept: false } && m.Measured is { } before && kept != before)
        {
            var past = kept - before > t.Headroom;
            if (past && !m.PastHeadroom) Log?.Invoke($"{LeafOf(m.Target)}: the game wrote more than the headroom since the last placement: the slot stays empty");
            m.PastHeadroom = past;
        }
        if (m.PastHeadroom) limit = 0;
        var e = Existing.Read(dir.At, name);
        var was = m.Files.FirstOrDefault(f => f.Slot && f.Name == name);
        var o = new Out(t with { Name = name, Limit = Math.Max(limit, GlCacheFile.HeaderSize) }, e) { Ours = e.Exists && (was ?? NoteSlot(note, name)) is { } f && SlotOurs(f, e) };
        if (e.Exists && !o.Ours)
        {
            e.Dispose();
            throw new Refused(SteamPlaceState.Unmerged, $"Steam hasn't merged {name} into its merged file yet");
        }
        return o;
    }

    /// <summary>Once the files have their names: the slot's identity as placed, a slot left empty forgotten, and the keys
    /// of the slots no longer listed. The identity is the one <see cref="Commit"/> found at the names while it held the files
    /// it wrote (<see cref="Out.Named"/>), or the one read of a slot that came out the same and kept its name: never what
    /// the names hold once the files are let go.</summary>
    void Committed(Manifest? m, Manifest now, List<Out> outs)
    {
        foreach (var o in outs.Where(o => o.Target.Slot && o.Count > 0))
            if (now.Files.FirstOrDefault(f => f.Name == o.Target.Name) is { Ours.Count: > 0 } f)
                (f.TocStat, f.BinStat, f.Toc, f.Bin) = !o.Write ? (o.Existing.TocStat, o.Existing.BinStat, o.Existing.TocLength, o.Existing.BinLength)
                    : o is { Named: { } named, TocWritten: { } t, BinWritten: { } b } ? (named.Toc, named.Bin, t.Length, b.Length)
                    : throw new Refused(SteamPlaceState.Failed, $"{f.Name} has no identity as placed");
        var empty = now.Files.Where(f => f.Slot && f.Ours.Count == 0).Select(f => f.Name).ToList();
        now.Files.RemoveAll(f => f.Slot && f.Ours.Count == 0);
        foreach (var name in empty.Concat(m?.Files.Where(f => f.Slot).Select(f => f.Name) ?? []).Distinct().Where(n => !now.Files.Any(f => f.Name == n)))
            File.Delete(OursFile(name));
    }

    /// <summary>The slot's manifest entries hold: each placed slot is still as written, and a slot learned since gets what
    /// didn't fit.</summary>
    bool SlotUpToDate(Manifest m, Folder dir)
    {
        if (m.Files.Any(f => f.Slot && !Unchanged(f, dir.At))) return false;
        return SlotOf(m) is not { } n || m.Last is not { Left: > 0 } || m.PastHeadroom || m.Files.Any(f => f.Slot && f.Name == SlotName(n));
    }

    /// <summary>The log line of a placement with the slot: what the merged file keeps that isn't SCSKiller's, the slot's
    /// share and limit, and the time it took.</summary>
    static string Replaced(string leaf, List<Out> outs, List<FileEntry> carried, TimeSpan took)
    {
        var merged = outs.FirstOrDefault(o => o.Target.SteamFirst);
        var own = outs.FirstOrDefault(o => o.Target.Slot);
        return $"{leaf}: placed with the game's own slot in {took.TotalSeconds:0.0} s"
            + (merged is { Kept: false } ? $", {merged.Target.Name} keeps {merged.Steam.Count:N0} entries of Steam's and the game's ({Format.Bytes(merged.SteamBytes)})" : "")
            + (own != null ? $", {own.Target.Name} {own.Warm.Count:N0} pipelines ({Format.Bytes(own.WarmBytes)}) of {Format.Bytes(own.Target.Limit)}" : "")
            + string.Concat(carried.Select(f => $", {f.Name} left as it is"));
    }

    /// <summary>A .bin past 4 GiB as Steam's merge writes it: its entries one after the other from its header on, in .toc
    /// order, each checked (magic, the key and size its record names, the payload's checksum), the .toc's offsets theirs
    /// modulo 4 GiB. Their records with their places in the .bin; null, with why, when one doesn't check or the .bin holds
    /// more.</summary>
    static List<Record>? Walk(SafeFileHandle bin, List<Record> toc, long length, out string? why)
    {
        var records = new List<Record>(toc.Count);
        var entry = new byte[1 << 20];
        long at = GlCacheFile.HeaderSize;
        foreach (var r in toc)
        {
            why = r.Offset != (uint)at ? "its .toc's offsets aren't where its entries lie"
                : r.Size > MaxEntry || at + r.Length > length ? "an entry lies outside its .bin"
                : null;
            if (why != null) return null;
            if (r.Length > entry.Length) entry = new byte[Math.Max(r.Length, 2L * entry.Length)];
            var span = entry.AsSpan(0, (int)r.Length);
            for (var read = 0; read < span.Length;)
            {
                var n = RandomAccess.Read(bin, span[read..], at + read);
                if (n == 0) break;
                read += n;
            }
            if (!GlCacheFile.Checks(span, r))
            {
                why = "an entry isn't the one its .toc names";
                return null;
            }
            records.Add(r with { Offset = at });
            at += r.Length;
        }
        why = at != length ? "its .bin holds more than its .toc names" : null;
        return why == null ? records : null;
    }
}
