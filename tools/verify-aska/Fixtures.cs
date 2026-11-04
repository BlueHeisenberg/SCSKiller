using System.Buffers.Binary;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Aska;
using SCSKiller.Core.Carved;
using static Check;

static class Fixtures
{
    const int Sector = 2048;
    const int Library = 1 << 20;
    const int Dictionary = Library + 36;
    const int Record = Dictionary + 8192;

    public static void Verify(AskaReader reader, string folder)
    {
        Directory.CreateDirectory(folder);
        var game = new Game(
            "manual:fixture",
            "synthetic ASKA",
            Store.Manual,
            folder,
            Path.Combine(folder, "anything.exe"));
        var path = Path.Combine(folder, "0000.bin");
        var shader = Shader(0);
        var raw = Shader(5);
        var utility = Shader(2);
        var decoy = Shader(3);

        // Match starts in the dictionary, overlaps output, then literals carry a synthetic DXBC container.
        var tokens = new List<(bool Literal, ushort Word)> { (false, 0x4001), (false, 0x0008) };
        for (var i = 4; i < shader.Length - 32; i += 2)
        {
            tokens.Add((true, BinaryPrimitives.ReadUInt16BigEndian(shader.AsSpan(i))));
        }

        tokens.Add((false, 0xe001)); // overlap a run of zero halfwords inside the shader
        tokens.Add((false, 0));

        var packed = new List<byte>();
        foreach (var group in tokens.Chunk(16))
        {
            var flags = (ushort)group.Select((t, i) => t.Literal ? 1 << i : 0).Sum();
            packed.Add((byte)(flags >> 8));
            packed.Add((byte)flags);

            foreach (var t in group)
            {
                packed.Add((byte)(t.Word >> 8));
                packed.Add((byte)t.Word);
            }
        }

        var payload = packed.ToArray();
        var bytes = new byte[Library + 4 * Sector];
        var head = new byte[72];
        Put(head, 0, 1);
        Put(head, 4, 1);
        Put(head, 8, 0x34829314);
        Allocation(head, 1, Library, 5 * Sector); // dictionary, record and padding
        Array.Resize(ref bytes, Library + 8 * Sector);
        Allocation(head, 2, Library + 5 * Sector, Sector);
        Allocation(head, 4, Library + 6 * Sector, Sector);

        // Synthetic fill, independent of any game's bytes. Preserve its phase in the obfuscated header.
        var pattern = Enumerable.Range(0, 192).Select(i => (byte)(i * 37 + 19)).ToArray();
        for (var i = 0; i < Library; i++)
        {
            bytes[i] = pattern[i % pattern.Length];
        }

        for (var i = 0; i < head.Length; i++)
        {
            bytes[i] ^= head[i];
        }

        "11hA"u8.CopyTo(bytes.AsSpan(Library));
        Put(bytes, Library + 8, 0x8f);
        Put(bytes, Library + 12, 2);
        decoy.CopyTo(bytes, Dictionary); // training fragments are not library entries
        "DXBC"u8.CopyTo(bytes.AsSpan(Dictionary + 8188));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(Record + 2), 32);
        Put(bytes, Record + 4, (uint)(32 + payload.Length));
        Put(bytes, Record + 8, (uint)(32 + 12 + shader.Length));
        bytes[Record + 14] = 3;
        payload.CopyTo(bytes, Record + 32);
        bytes.AsSpan(Record, 32 + payload.Length).CopyTo(bytes.AsSpan(Record + 32 + payload.Length));
        raw.CopyTo(bytes, Library + 5 * Sector);
        utility.CopyTo(bytes, Library + 6 * Sector);
        decoy.CopyTo(bytes, Library + 7 * Sector); // an unrelated asset after the boot resources
        File.WriteAllBytes(path, bytes);

        using (var sparse = File.OpenWrite(path))
        {
            sparse.SetLength(12L << 30); // never scan the whole game archive
        }

        var engine = reader.Detect(game) ?? throw new Exception("disc not detected");
        Require(engine.Unsupported == null, "legacy format supported without a game-specific executable");
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var wanted = new[] { shader, raw, utility }.Select(Sha).ToHashSet();
        Require(
            index.Shaders.Keys.ToHashSet().SetEquals(wanted),
            "only compressed and allocated raw boot shaders indexed");

        var selected = new HashSet<string> { Sha(shader) };
        var received = new List<byte[]>();
        reader.ReadShaders(game, engine, selected, (_, b) => received.Add(b), CancellationToken.None);
        Require(
            received.Count == 1 && received[0].SequenceEqual(shader),
            "dictionary match and literals decode to exact requested bytes");

        received.Clear();
        reader.ReadShaders(
            game,
            engine,
            new HashSet<string> { Sha(shader), new('0', 40) },
            (_, b) => received.Add(b),
            CancellationToken.None);
        Require(
            received.Count == 1 && received[0].SequenceEqual(shader),
            "absent requested hashes do not prevent delivery of available shaders");

        // Unknown versions, truncated allocations and malformed records must fail, never index partial coverage.
        Put(bytes, Library + 8, 0x12345678);
        File.WriteAllBytes(path, bytes);
        Require(reader.Detect(game)?.Unsupported != null, "unknown format reported as unsupported");
        Throws<InvalidDataException>(() => reader.Index(game, engine, null, CancellationToken.None));
        Put(bytes, Library + 8, 0x8f);

        CheckBadRecord(Record + 4, 64 << 20);
        CheckBadRecord(Record + 8, 33); // output overrun
        CheckBadRecord(Record + 8, 8192); // output underrun

        var saved = bytes[Record + 32];
        bytes[Record + 32] = 0;
        var tokenSaved = bytes[Record + 35];
        bytes[Record + 35] = 0; // premature end token
        File.WriteAllBytes(path, bytes);
        Throws<InvalidDataException>(() => reader.Index(game, engine, null, CancellationToken.None));
        bytes[Record + 32] = saved;
        bytes[Record + 35] = tokenSaved;

        File.WriteAllBytes(path, bytes[..(Library + Sector)]);
        Throws<InvalidDataException>(() => reader.Index(game, engine, null, CancellationToken.None));
        File.WriteAllBytes(path, new byte[8192]);
        Require(
            reader.Detect(game) == null && reader.IndexStamp(game) == "",
            "unrelated 0000.bin is not ASKA and does not change another engine's stamp");
        Console.WriteLine(
            "PASS synthetic disc allocations, legacy LZ, selective retrieval, missing shader, "
            + "unknown format and corrupt input checks");

        void CheckBadRecord(int at, uint value)
        {
            var saved = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            Put(bytes, at, value);
            File.WriteAllBytes(path, bytes);
            Throws<InvalidDataException>(() => reader.Index(game, engine, null, CancellationToken.None));
            Put(bytes, at, saved);
        }
    }

    static void Allocation(byte[] head, int slot, int offset, int size)
    {
        Put(head, 12 + slot * 12, (uint)(size / Sector));
        Put(head, 16 + slot * 12, (uint)size);
        Put(head, 20 + slot * 12, (uint)(offset / Sector));
    }

    static byte[] Shader(uint kind)
    {
        var program = new byte[40];
        Put(program, 0, kind << 16 | 0x50);
        Put(program, 4, 10);
        return Dxbc.Container("SHEX"u8, program);
    }

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    static void Put(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), value);
}
