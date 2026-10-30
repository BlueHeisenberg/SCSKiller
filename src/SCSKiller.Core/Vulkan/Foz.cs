using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Vulkan;

/// <summary>Fossilize's stream archive (.foz, fossilize_db.cpp StreamArchive, v6, readable from v5): a 16-byte magic, then
/// entries of a 40-char hex name (24 tag + 16 hash), u32 stored size, u32 format (1 raw, 2 zlib), u32 CRC-32 of the stored
/// bytes (0 = unchecked), u32 size unpacked, then the stored bytes. Append-only: a torn last entry is dropped, a repeated
/// name ignored. SCSKiller keeps a Vulkan game's recording in it as the layer wrote it.</summary>
public static class Foz
{
    public const int ApplicationInfo = 0, ShaderModule = 4, Graphics = 6, Compute = 7, BlobLink = 8, RayTracing = 9, BucketInfo = 10, TagCount = 11;
    public const int Version = 6, MinVersion = 5;
    const int NameLength = 40, HeadLength = NameLength + 16;
    /// <summary>The largest payload read, packed or not: a pipeline's or a module's JSON and SPIR-V are far smaller.</summary>
    public const int MaxPayload = 64 << 20;
    /// <summary>The most entries an archive may list: a large game's recording holds a few hundred thousand.</summary>
    public const int MaxEntries = 4_000_000;
    /// <summary>The largest inbox staged for an import.</summary>
    public const long MaxInbox = 4L << 30;

    static readonly byte[] Magic = [0x81, .. "FOSSILIZEDB"u8, 0, 0, 0];

    public static bool IsPipeline(int tag) => tag is Graphics or Compute or RayTracing;

    /// <summary>One entry: where its name starts and its whole length, name and header included.</summary>
    public readonly record struct Entry(int Tag, ulong Hash, long Offset, long Length)
    {
        public string Name => Foz.Name(Tag, Hash);
    }

    /// <summary>The entry name, as Fossilize writes it and SCSKiller keys a Vulkan item ('V' records): lowercase hex.</summary>
    public static string Name(int tag, ulong hash) => $"{tag:x24}{hash:x16}";

    /// <summary>The entries in file order, a repeated name once; the torn last one and tags Fossilize doesn't know left out.
    /// InvalidDataException for a file that isn't a .foz of a version this reads.</summary>
    public static List<Entry> List(Stream f) => List(f, out _);

    /// <param name="end">where the last whole entry ends: an append goes there</param>
    public static List<Entry> List(Stream f, out long end)
    {
        var list = new List<Entry>();
        end = Walk(f, e =>
        {
            list.Add(e);
            return true;
        });
        return list;
    }

    /// <summary>The first entry in file order that <paramref name="match"/> takes, reading no further; null when none
    /// does. InvalidDataException as <see cref="List(Stream)"/>.</summary>
    public static Entry? First(Stream f, Func<Entry, bool> match)
    {
        Entry? found = null;
        Walk(f, e =>
        {
            if (!match(e)) return true;
            found = e;
            return false;
        });
        return found;
    }

    /// <summary>Hands <paramref name="each"/> the entries in file order, a repeated name once, until it returns false or
    /// the archive ends. Returns where the last whole entry it read ends.</summary>
    static long Walk(Stream f, Func<Entry, bool> each)
    {
        var head = new byte[HeadLength];
        f.Position = 0;
        if (f.ReadAtLeast(head.AsSpan(0, Magic.Length + 1), Magic.Length + 1, false) < Magic.Length + 1 || !head.AsSpan(0, Magic.Length).SequenceEqual(Magic)
            || head[Magic.Length] is < MinVersion or > Version)
            throw new InvalidDataException("not a Fossilize v5 or v6 archive");
        var seen = new HashSet<(int, ulong)>();
        long at = Magic.Length + 1, length = f.Length;
        while (at + HeadLength <= length)
        {
            f.Position = at;
            f.ReadExactly(head);
            var stored = (long)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(NameLength));
            if (stored > MaxPayload) throw new InvalidDataException($"an entry of {stored} bytes at byte {at}");
            if (at + HeadLength + stored > length) break;   // torn: the recorder was still writing, or was killed
            if (!ulong.TryParse(Encoding.ASCII.GetString(head, 0, 24), System.Globalization.NumberStyles.HexNumber, null, out var tag)
                || !ulong.TryParse(Encoding.ASCII.GetString(head, 24, 16), System.Globalization.NumberStyles.HexNumber, null, out var hash))
                throw new InvalidDataException($"an entry name that isn't hex at byte {at}");
            var entry = new Entry((int)tag, hash, at, HeadLength + stored);
            at += HeadLength + stored;
            if (tag < TagCount && seen.Add(((int)tag, hash)))
            {
                if (seen.Count > MaxEntries) throw new InvalidDataException($"over {MaxEntries:N0} entries");
                if (!each(entry)) break;
            }
        }
        return at;
    }

    public static List<Entry> List(string path)
    {
        using var f = OpenShared(path);
        return List(f);
    }

    /// <summary>Read alongside the recorder, which holds its file open for writing while the game runs.</summary>
    public static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>The entry's payload, unpacked. InvalidDataException when it can't be.</summary>
    public static byte[] Payload(Stream f, Entry e)
    {
        var head = new byte[HeadLength];
        f.Position = e.Offset;
        f.ReadExactly(head);
        var (stored, format, size) = (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(NameLength)),
            BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(NameLength + 4)), BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(NameLength + 12)));
        if (stored > MaxPayload || size > MaxPayload) throw new InvalidDataException($"{e.Name}: a payload over {MaxPayload >> 20} MB");
        var raw = new byte[stored];
        f.ReadExactly(raw);
        if (format == 1) return raw;
        if (format != 2) throw new InvalidDataException($"{e.Name}: unknown format {format}");
        var payload = new byte[size];
        using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
        if (z.ReadAtLeast(payload, payload.Length, false) != payload.Length || z.ReadByte() >= 0) throw new InvalidDataException($"{e.Name}: unpacks to another size than its header's");
        return payload;
    }

    /// <summary>Appends an entry, zlib-packed, CRC 0 (unchecked, as Fossilize writes by default).</summary>
    public static void Append(Stream f, int tag, ulong hash, byte[] payload)
    {
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(payload);
        var head = new byte[HeadLength];
        Encoding.ASCII.GetBytes(Name(tag, hash), head);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(NameLength), (uint)packed.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(NameLength + 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(NameLength + 12), (uint)payload.Length);
        f.Write(head);
        f.Write(packed.GetBuffer(), 0, (int)packed.Length);
    }

    /// <summary>Appends an entry with no payload, stored raw, as Fossilize's replayer writes its validation whitelist: only
    /// the name counts.</summary>
    public static void AppendName(Stream f, int tag, ulong hash)
    {
        var head = new byte[HeadLength];
        Encoding.ASCII.GetBytes(Name(tag, hash), head);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(NameLength + 4), 1);
        f.Write(head);
    }

    /// <summary>A new archive's magic.</summary>
    public static void WriteMagic(Stream f)
    {
        f.Write(Magic);
        f.WriteByte(Version);
    }

    /// <summary>Copies an entry as stored, byte for byte.</summary>
    public static void Copy(Stream from, Entry e, Stream to)
    {
        from.Position = e.Offset;
        var buffer = new byte[(int)Math.Min(e.Length, 1 << 20)];
        for (var left = e.Length; left > 0;)
        {
            var n = (int)Math.Min(left, buffer.Length);
            from.ReadExactly(buffer, 0, n);
            to.Write(buffer, 0, n);
            left -= n;
        }
    }

    /// <summary>Appends to <paramref name="store"/> (created when missing) the entries of <paramref name="from"/> it lacks, in
    /// their order, after cutting a torn tail off the store. Returns the entries added (as <paramref name="from"/> holds them). The store's version
    /// stays what it was: entries don't change between v5 and v6.</summary>
    public static List<Entry> Merge(string store, Stream from)
    {
        var theirs = List(from);
        using var f = new FileStream(store, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        List<Entry> ours = [];
        long end;
        if (f.Length == 0)
        {
            WriteMagic(f);
            end = f.Position;
        }
        else ours = List(f, out end);
        var have = ours.Select(e => (e.Tag, e.Hash)).ToHashSet();
        var add = theirs.Where(e => !have.Contains((e.Tag, e.Hash))).ToList();
        if (add.Count == 0) return [];
        f.SetLength(end);
        f.Position = end;
        var b = new BufferedStream(f, 1 << 20);   // not disposed: f is
        foreach (var e in add) Copy(from, e, b);
        b.Flush();
        return add;
    }

    /// <summary>The tags a shared Vulkan entry carries and the JSON section each one's object holds (the server's too):
    /// samplers, set and pipeline layouts, render passes and pipelines. Never a shader module, which is shader code, the
    /// application info, which holds this PC's device features, or Steam's links. The replayer reads every section a JSON
    /// has, whatever its tag.</summary>
    static readonly (int Tag, string Section)[] Sections =
        [(1, "samplers"), (2, "setLayouts"), (3, "pipelineLayouts"), (5, "renderPasses"), (5, "renderPasses2"), (Graphics, "graphicsPipelines"), (Compute, "computePipelines"), (RayTracing, "raytracingPipelines")];
    /// <summary>The record tag of a shared entry: its name, then its JSON unpacked.</summary>
    public const char SharedTag = 'F';
    /// <summary>A shared entry's JSON, at most (the server's): a recorded pipeline's is a few KB.</summary>
    public const int MaxShared = 1 << 20;

    /// <summary>The archive's entries a Vulkan upload carries, as <see cref="SharedTag"/> records in file order; one that
    /// can't be unpacked or isn't <see cref="IsShared"/> stays out.</summary>
    public static List<PsoDb.Rec> SharedRecords(string path)
    {
        using var f = OpenShared(path);
        var list = new List<PsoDb.Rec>();
        foreach (var e in List(f).Where(e => Sections.Any(s => s.Tag == e.Tag)))
        {
            byte[] p;
            try { p = Payload(f, e); }
            catch (InvalidDataException) { continue; }
            if (IsShared(e.Tag, e.Hash, p)) list.Add(new(SharedTag, [.. Encoding.ASCII.GetBytes(e.Name), .. p]));
        }
        return list;
    }

    /// <summary>A downloaded Vulkan entry's records as an archive, with its pipeline count; null when a record isn't a
    /// shared entry's. One whose JSON isn't <see cref="IsShared"/> is left out, as is a name held with two payloads:
    /// neither is trusted over the other.</summary>
    public static byte[]? FromShared(IReadOnlyList<PsoDb.Rec> records, out int pipelines)
    {
        pipelines = 0;
        var byName = new Dictionary<string, (int Tag, ulong Hash, byte[]? Json)>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r.Tag != SharedTag || r.Payload.Length <= NameLength) return null;
            var name = Encoding.ASCII.GetString(r.Payload, 0, NameLength);
            if (!name.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
                || !ulong.TryParse(name[..24], System.Globalization.NumberStyles.HexNumber, null, out var tag) || tag > TagCount
                || !ulong.TryParse(name[24..], System.Globalization.NumberStyles.HexNumber, null, out var hash))
                return null;
            var json = r.Payload[NameLength..];
            if (!IsShared((int)tag, hash, json)) continue;
            byName[name] = byName.TryGetValue(name, out var had) && had.Json?.AsSpan().SequenceEqual(json) != true ? (had.Tag, had.Hash, null) : ((int)tag, hash, json);
        }
        var m = new MemoryStream();
        WriteMagic(m);
        foreach (var (tag, hash, json) in byName.Values)
            if (json != null)
            {
                Append(m, tag, hash, json);
                if (IsPipeline(tag)) pipelines++;
            }
        return m.ToArray();
    }

    /// <summary>An entry's JSON a Vulkan upload may carry: at most <see cref="MaxShared"/> bytes, an object of an integer
    /// "version" and its tag's section (<see cref="Sections"/>), which holds the named object alone.</summary>
    static bool IsShared(int tag, ulong hash, byte[] json)
    {
        if (json.Length > MaxShared) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var props = doc.RootElement.EnumerateObject().ToList();
            if (props.Count != 2 || props.Count(p => p.Name == "version") != 1) return false;
            var (version, section) = props[0].Name == "version" ? (props[0], props[1]) : (props[1], props[0]);
            return version.Value.ValueKind == JsonValueKind.Number && version.Value.TryGetInt32(out _) && Sections.Contains((tag, section.Name))
                && section.Value.ValueKind == JsonValueKind.Object && section.Value.EnumerateObject().Select(o => o.Name).SequenceEqual([$"{hash:x16}"]);
        }
        catch (JsonException) { return false; }
    }
}
