using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace SCSKiller.Core.BlackSpace;

/// <summary>The hash and cipher of Pearl Abyss' archives (<see cref="Pamt"/>). Names hash with Bob Jenkins' lookup3
/// hashlittle, seeded <see cref="Seed"/>. An encrypted entry is ChaCha20 (32-bit block counter, 96-bit nonce), keyed by
/// its lowercase file name's hash h: key byte i = the 32-byte base's byte i ^ h's byte i % 4 ^ the group's key byte
/// (<see cref="Pamt.KeyByte"/>) ^ <see cref="PublishedKeyByte"/>, counter and nonce words all h. The base is the game's
/// key as published, which only the user gives (<see cref="BlackSpaceKeys"/>).</summary>
public static class PazCrypto
{
    public const uint Seed = 0xC5EDE;

    /// <summary>The key byte of most groups, the shader cache's among them: the published base has it folded in.</summary>
    public const byte PublishedKeyByte = 0x6D;

    public static uint Hash(string s, uint seed = Seed) => Hash(Encoding.UTF8.GetBytes(s), seed);

    public static uint Hash(ReadOnlySpan<byte> k, uint seed = Seed)
    {
        uint a, b, c;
        a = b = c = 0xDEADBEEF + (uint)k.Length + seed;
        while (k.Length > 12)
        {
            a += BinaryPrimitives.ReadUInt32LittleEndian(k);
            b += BinaryPrimitives.ReadUInt32LittleEndian(k[4..]);
            c += BinaryPrimitives.ReadUInt32LittleEndian(k[8..]);
            a -= c; a ^= BitOperations.RotateLeft(c, 4); c += b;
            b -= a; b ^= BitOperations.RotateLeft(a, 6); a += c;
            c -= b; c ^= BitOperations.RotateLeft(b, 8); b += a;
            a -= c; a ^= BitOperations.RotateLeft(c, 16); c += b;
            b -= a; b ^= BitOperations.RotateLeft(a, 19); a += c;
            c -= b; c ^= BitOperations.RotateLeft(b, 4); b += a;
            k = k[12..];
        }
        if (k.IsEmpty) return c;
        Span<byte> t = stackalloc byte[12];
        t.Clear();
        k.CopyTo(t);
        a += BinaryPrimitives.ReadUInt32LittleEndian(t);
        b += BinaryPrimitives.ReadUInt32LittleEndian(t[4..]);
        c += BinaryPrimitives.ReadUInt32LittleEndian(t[8..]);
        c ^= b; c -= BitOperations.RotateLeft(b, 14);
        a ^= c; a -= BitOperations.RotateLeft(c, 11);
        b ^= a; b -= BitOperations.RotateLeft(a, 25);
        c ^= b; c -= BitOperations.RotateLeft(b, 16);
        a ^= c; a -= BitOperations.RotateLeft(c, 4);
        b ^= a; b -= BitOperations.RotateLeft(a, 14);
        c ^= b; c -= BitOperations.RotateLeft(b, 24);
        return c;
    }

    /// <summary>Decrypts an entry named <paramref name="fileName"/> (any folder, any case) in place.</summary>
    public static void Decrypt(Span<byte> data, string fileName, ReadOnlySpan<byte> keyBase, byte keyByte)
    {
        if (keyBase.Length != 32) throw new ArgumentException("a 32-byte base", nameof(keyBase));
        var h = Hash(Path.GetFileName(fileName.Replace('\\', '/')).ToLowerInvariant());
        Span<byte> seed = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seed, h);
        Span<uint> key = stackalloc uint[8];
        Span<byte> k = stackalloc byte[32];
        for (var i = 0; i < 32; i++) k[i] = (byte)(keyBase[i] ^ seed[i % 4] ^ keyByte ^ PublishedKeyByte);
        for (var i = 0; i < 8; i++) key[i] = BinaryPrimitives.ReadUInt32LittleEndian(k[(4 * i)..]);
        ChaCha20(data, key, h, [h, h, h]);
    }

    /// <summary>ChaCha20 (RFC 8439's block function) XORed over <paramref name="data"/> from block <paramref name="counter"/>.</summary>
    public static void ChaCha20(Span<byte> data, ReadOnlySpan<uint> key, uint counter, ReadOnlySpan<uint> nonce)
    {
        Span<uint> init = stackalloc uint[16];
        Span<uint> x = stackalloc uint[16];
        Span<byte> stream = stackalloc byte[64];
        init[0] = 0x61707865; init[1] = 0x3320646e; init[2] = 0x79622d32; init[3] = 0x6b206574;
        key.CopyTo(init[4..]);
        nonce.CopyTo(init[13..]);
        for (var at = 0; at < data.Length; at += 64, counter++)
        {
            init[12] = counter;
            init.CopyTo(x);
            for (var r = 0; r < 10; r++)
            {
                Quarter(x, 0, 4, 8, 12); Quarter(x, 1, 5, 9, 13); Quarter(x, 2, 6, 10, 14); Quarter(x, 3, 7, 11, 15);
                Quarter(x, 0, 5, 10, 15); Quarter(x, 1, 6, 11, 12); Quarter(x, 2, 7, 8, 13); Quarter(x, 3, 4, 9, 14);
            }
            for (var i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(stream[(4 * i)..], x[i] + init[i]);
            var block = data[at..Math.Min(data.Length, at + 64)];
            for (var i = 0; i < block.Length; i++) block[i] ^= stream[i];
        }
    }

    static void Quarter(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 7);
    }
}
