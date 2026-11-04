using System.Buffers.Binary;

namespace SCSKiller.Core.Aska;

/// <summary>
/// The ASKA virtual disc's first five allocation slots: metadata, shader library, raw shaders, texture,
/// and utility programs. Only these boot resources are read, not the game's remaining asset allocations.
/// </summary>
internal static class AskaDisc
{
    internal readonly record struct Allocation(long Offset, int Size);

    public static Allocation[]? Boot(Stream f)
    {
        // Unwritten space repeats a 192-byte fill pattern. The header is XORed with that same pattern.
        // Recover it from padding, preserving its phase, and require the decoded disc signature, not just periodic data.
        var b = new byte[(int)Math.Min(f.Length, 64 << 10)];
        f.Position = 0;
        f.ReadExactly(b);

        var start = 192;
        for (var i = 192; i <= b.Length; i++)
        {
            if (i < b.Length && b[i] == b[i - 192])
            {
                continue;
            }

            if (i - start >= 1024)
            {
                var head = new byte[72];
                for (var j = 0; j < head.Length; j++)
                {
                    head[j] = (byte)(b[j] ^ b[start + (j - start % 192 + 192) % 192]);
                }

                if (U(head, 0) == 1 && U(head, 4) == 1 && U(head, 8) == 0x34829314)
                {
                    return Allocations(head, f.Length);
                }
            }

            start = i + 1;
        }

        return null;
    }

    static Allocation[] Allocations(byte[] header, long length)
    {
        var blocks = U(header, 24);
        var bytes = U(header, 28);
        var sector = blocks == 0 ? 0 : bytes / blocks;
        if (sector is < 512 or > 65536 || (sector & (sector - 1)) != 0 || (long)blocks * sector != bytes)
        {
            throw new InvalidDataException("ASKA disc has an unsupported allocation size");
        }

        var result = new Allocation[5];
        long end = 72;
        for (var i = 0; i < result.Length; i++)
        {
            var at = 12 + 12 * i;
            var size = U(header, at + 4);
            var offset = (long)U(header, at + 8) * sector;
            if (size == 0 && U(header, at) == 0 && offset == 0)
            {
                continue;
            }

            if ((long)U(header, at) * sector != size || size > int.MaxValue || offset < end || offset + size > length)
            {
                throw new InvalidDataException($"ASKA boot allocation {i} has invalid bounds");
            }

            result[i] = new(offset, (int)size);
            end = offset + size;
        }

        return result;
    }

    static uint U(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
}
