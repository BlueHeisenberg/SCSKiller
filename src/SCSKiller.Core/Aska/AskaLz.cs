using System.Buffers.Binary;

namespace SCSKiller.Core.Aska;

/// <summary>
/// The older ASKA library codec: big-endian flags and tokens, copying literal halfwords or dictionary-backed
/// matches. Output bytes keep their order. A match may overlap itself or cross from the 8 KiB dictionary into output.
/// </summary>
internal static class AskaLz
{
    public static void Decode(ReadOnlySpan<byte> source, ReadOnlySpan<byte> dictionary, Span<byte> output)
    {
        int input = 0;
        int written = 0;

        while (input + 2 <= source.Length)
        {
            var flags = BinaryPrimitives.ReadUInt16BigEndian(source[input..]);
            input += 2;

            for (var bit = 0; bit < 16; bit++, flags >>= 1)
            {
                if (input + 2 > source.Length)
                {
                    throw new InvalidDataException("truncated ASKA LZ token");
                }

                var token = BinaryPrimitives.ReadUInt16BigEndian(source[input..]);
                input += 2;

                if ((flags & 1) != 0)
                {
                    if (written + 2 > output.Length)
                    {
                        throw new InvalidDataException("ASKA LZ literal exceeds output");
                    }

                    source.Slice(input - 2, 2).CopyTo(output[written..]);
                    written += 2;
                    continue;
                }

                var distance = (token & 0xfff) * 2;
                if (distance == 0)
                {
                    if (input != source.Length || written != output.Length)
                    {
                        throw new InvalidDataException("ASKA LZ size mismatch");
                    }

                    return;
                }

                var size = ((token >> 12) + 2) * 2;
                if (written + size > output.Length || written - distance < -dictionary.Length)
                {
                    throw new InvalidDataException("ASKA LZ match exceeds dictionary or output");
                }

                for (var n = 0; n < size; n++)
                {
                    var from = written - distance;
                    output[written++] = from < 0 ? dictionary[dictionary.Length + from] : output[from];
                }
            }
        }

        throw new InvalidDataException("ASKA LZ stream has no end token");
    }
}
