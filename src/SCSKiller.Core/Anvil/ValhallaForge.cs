using System.Buffers.Binary;
using SCSKiller.Core.Decima;
using static SCSKiller.Core.Anvil.ValhallaReader;

namespace SCSKiller.Core.Anvil;

internal static class ValhallaForge
{
    const int IndexHeaderSize = 44, FileSetSize = 48, EntrySize = 20;
    const int MaxEntries = 100_000, MaxEntryBytes = 64 << 20, BlockBytes = 256 << 10;
    static ReadOnlySpan<byte> ChunkMagic => [0x33, 0xAA, 0xFB, 0x57, 0x99, 0xFA, 0x04, 0x10, 3, 0, 8, 0, 0, 4, 0x80];
    internal readonly record struct Entry(long Offset, ulong Id, int Size);

    // The two sampled v29 archives have one file set. Refuse a different layout instead of interpreting its offsets as v29.
    internal static List<Entry> Entries(FileStream file)
    {
        Span<byte> header = stackalloc byte[21];
        file.ReadExactly(header);
        if (!header.StartsWith("scimitar\0"u8) || U32(header[9..]) != 29)
            throw new InvalidDataException("expected a Scimitar v29 archive");
        file.Position = Position(file, header[13..], IndexHeaderSize);
        Span<byte> index = stackalloc byte[IndexHeaderSize];
        file.ReadExactly(index);
        if (U32(index[32..]) != 1) throw new InvalidDataException("expected one Forge file set");
        file.Position = Position(file, index[36..], FileSetSize);
        Span<byte> set = stackalloc byte[FileSetSize];
        file.ReadExactly(set);
        var count = U32(set);
        if (count > MaxEntries || count != U32(index)) throw new InvalidDataException("invalid Forge entry count");
        file.Position = Position(file, set[8..], count * EntrySize);
        Span<byte> bytes = stackalloc byte[EntrySize];
        var entries = new List<Entry>();
        for (var i = 0; i < count; i++)
        {
            file.ReadExactly(bytes);
            var size = U32(bytes[16..]);
            if (size < ChunkMagic.Length) continue;
            if (size > int.MaxValue) throw new InvalidDataException("Forge entry is too large");
            var offset = Position(file, bytes, size);
            entries.Add(new(offset, BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), (int)size));
        }
        return entries;
    }

    internal static byte[]? Read(FileStream file, Entry entry, CancellationToken ct)
    {
        var end = entry.Offset + entry.Size;
        if (end > file.Length) throw new InvalidDataException("truncated Forge entry");
        file.Position = entry.Offset;
        Span<byte> header = stackalloc byte[19];
        if (entry.Size < header.Length) return null;
        file.ReadExactly(header);
        if (!header.StartsWith(ChunkMagic)) return null; // The index also lists non-resource bookkeeping entries.
        SkipMetadata(file, header, end);
        if (end - file.Position < header.Length) throw new InvalidDataException("missing Forge payload");
        file.ReadExactly(header);
        var count = U32(header[15..]);
        if (!header.StartsWith(ChunkMagic) || count == 0 || count > MaxEntryBytes / BlockBytes || end - file.Position < count * 8L)
            throw new InvalidDataException("invalid Forge payload header");

        var blocks = new (int Raw, int Stored)[count];
        Span<byte> sizes = stackalloc byte[8];
        var unpacked = 0;
        long packed = 0;
        for (var i = 0; i < blocks.Length; i++)
        {
            file.ReadExactly(sizes);
            var raw = U32(sizes);
            var stored = U32(sizes[4..]);
            if (raw == 0 || raw > BlockBytes || stored == 0 || stored > BlockBytes + 64)
                throw new InvalidDataException("invalid Forge chunk size");
            blocks[i] = ((int)raw, (int)stored);
            unpacked += (int)raw;
            packed += 4 + stored;
        }
        if (packed != end - file.Position) throw new InvalidDataException("Forge chunks do not fill the entry");
        var result = new byte[unpacked];
        var offset = 0;
        byte[]? decoded = null;
        foreach (var (raw, stored) in blocks)
        {
            ct.ThrowIfCancellationRequested();
            file.Position += 4; // Per-block checksum.
            if (raw == stored) file.ReadExactly(result.AsSpan(offset, raw));
            else
            {
                var input = new byte[stored];
                file.ReadExactly(input);
                if (stored < 2 || (input[0] & 0x3f) != 0x0c || input[1] != 0x0a)
                    throw new InvalidDataException("expected an Oodle Mermaid/Selkie chunk");
                decoded ??= new byte[BlockBytes];
                DecimaArchive.Decompress(input, stored, decoded, raw);
                decoded.AsSpan(0, raw).CopyTo(result.AsSpan(offset, raw));
            }
            offset += raw;
        }
        return result;
    }

    static void SkipMetadata(FileStream file, ReadOnlySpan<byte> header, long end)
    {
        // The first block is a stored 16-byte entry descriptor. The second contains the compressed resource.
        Span<byte> sizes = stackalloc byte[8];
        if (U32(header[15..]) != 1 || end - file.Position < 28)
            throw new InvalidDataException("invalid Forge metadata header");
        file.ReadExactly(sizes);
        if (U32(sizes) != 16 || U32(sizes[4..]) != 16)
            throw new InvalidDataException("invalid Forge metadata size");
        file.Position += 20;
    }

    static long Position(FileStream file, ReadOnlySpan<byte> bytes, long size)
    {
        var position = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        return position >= 0 && position <= file.Length - size ? position : throw new InvalidDataException("invalid Forge offset");
    }
}
