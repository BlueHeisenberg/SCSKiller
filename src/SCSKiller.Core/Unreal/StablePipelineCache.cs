using System.Buffers.Binary;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Unreal;

/// <summary>Unreal's shipped pipeline cache (Content/PipelineCaches/&lt;Platform&gt;/&lt;Project&gt;_&lt;ShaderPlatform&gt;.stable.upipelinecache),
/// read from its table of contents: each PSO's type, the library hashes (FSHAHash) of its shaders and a graphics PSO's input
/// layout. Layout from Epic's PipelineFileCache.cpp (FPipelineCacheFileFormatHeader, FPipelineCacheFileFormatTOC, FPipelineCacheFileFormatPSOMetaData).</summary>
public static class StablePipelineCache
{
    /// <summary>Unreal 5.8's file version (PSOPrecacheResult).</summary>
    public const uint NewestVersion = 31;

    public enum PsoType : uint { Compute = 0, Graphics = 1, RayTracing = 2 }

    /// <param name="Layout">a graphics PSO's input layout (<see cref="Declaration"/>); null: not read</param>
    public sealed record Pso(ulong Key, PsoType Type, string[] Shaders, IReadOnlyList<LayoutElem>? Layout = null);

    const ulong Magic = 0x5049504543414348, TocMagic = 0x544F435354415232, TocMagic17 = 0x544F435354415254, EofMagic = 0x454F462D4D41524B; // PIPECACH, TOCSTAR2, TOCSTART, EOF-MARK

    /// <summary>File versions 22 (LastUsedTime) to 31 (PSOPrecacheResult) share this header and TOC layout; between them only
    /// the PSO bodies change, of which this reads the type and a graphics PSO's vertex declaration (29 adds the ray tracing
    /// shader binding layout, 30 the graphics VRS fields, 31 a precache result after the type). 31 (UE 5.8) lists 8-byte shader hashes (FXxHash64), named
    /// as CUE4Parse names a 5.8 library's: zero-padded to 20 bytes. Version 17 (Subpass, UE 4.25) has the TOCSTART table: no
    /// guid shared by every entry, no last used time per entry. A fork writes 8-byte entry keys and PSO hashes (Lost Records:
    /// Bloom &amp; Rage, version 26), read when the 4-byte layout doesn't end at EOF-MARK. Null: not such a file, or a later
    /// version (<see cref="Version"/>).</summary>
    public static List<Pso>? Read(ReadOnlySpan<byte> b) => Read(b, 4) ?? (Version(b) is not 17 ? Read(b, 8) : null);

    static List<Pso>? Read(ReadOnlySpan<byte> b, int keyWidth)
    {
        try
        {
            if (b.Length < 57 || U64(b, 0) != Magic || U32(b, 8) is not (17 or (>= 22 and <= NewestVersion)) || U64(b, b.Length - 8) != EofMagic) return null;
            var o = checked((int)U64(b, 33)); // after magic, version, game version, u8 platform, guid
            var v17 = U32(b, 8) == 17;
            var width = U32(b, 8) >= 31 ? 8 : 20;
            if (U64(b, o) != (v17 ? TocMagic17 : TocMagic)) return null;
            o += 8;
            if (!v17) o += b[o] != 0 ? 17 : 1; // every entry's guid, once
            o += 4; // sort order
            var n = BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
            o += 4;
            if (n < 0 || n > b.Length / (v17 ? 86 : 94)) return null; // an entry is 86 or 94 bytes at least
            var psos = new List<Pso>(n);
            for (var i = 0; i < n; i++)
            {
                var key = keyWidth == 8 ? U64(b, o) : U32(b, o);
                var body = checked((int)U64(b, o + keyWidth));
                var size = U64(b, o + keyWidth + 8);
                o += keyWidth + 16 + 16 + 32 + keyWidth; // key, file offset + size, guid, stats (their PSO hash as wide as the key)
                var k = BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
                o += 4;
                if (k is < 0 or > 8) return null; // a PSO has at most five stages
                var shaders = new string[k];
                for (var j = 0; j < k; j++) shaders[j] = Convert.ToHexStringLower(b.Slice(o + width * j, width)).PadRight(40, '0');
                o += width * k + 8 + 2 + (v17 ? 0 : 8); // shaders, usage mask, engine flags, last used time
                var type = (PsoType)U32(b, body);
                var layout = type == PsoType.Graphics && !v17 && size <= (ulong)(b.Length - body) ? Declaration(b.Slice(body, (int)size), shaders, width) : null;
                psos.Add(new Pso(key, type, shaders, layout));
            }
            return o == b.Length - 8 ? psos : null;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException) { return null; }
    }

    /// <summary>Unreal's EVertexElementType (VET_Float1 = 1 to VET_UInt = 20) as its D3D12 RHI creates it, DXGI_FORMAT.</summary>
    static readonly uint[] VertexFormats = [0, 41, 16, 6, 2, 31, 30, 28, 87, 38, 14, 37, 34, 10, 13, 36, 12, 35, 11, 24, 42];

    /// <summary>A graphics body's vertex declaration (FVertexDeclarationElementList) as the D3D12 RHI creates the PSO's input
    /// layout: each element ATTRIBUTE&lt;attribute&gt; in the file's order (stream, then offset, as D3D12 sorts it). The body is
    /// the type, from version 30 four more bytes, the VS, PS, GS, MS and AS hashes, then the declaration: u32 count, per element
    /// u8 stream, u8 offset, u8 EVertexElementType, u8 attribute, u16 stride, u16 instanced. Measured against recordings of the
    /// same stage sets (versions 25, 28, 30): every one equal. Null when the hashes aren't the TOC's at either place or an
    /// element doesn't map, repeats an attribute or is misaligned.</summary>
    static List<LayoutElem>? Declaration(ReadOnlySpan<byte> body, string[] shaders, int width)
    {
        foreach (var at in (ReadOnlySpan<int>)[4, 8])
        {
            var o = at + 5 * width;
            if (body.Length < o + 4 || !SameShaders(body.Slice(at, 5 * width), shaders, width)) continue;
            var n = U32(body, o);
            if (n > 32 || body.Length < o + 4 + 8 * (int)n) return null; // D3D12 takes 32 elements at most
            var layout = new List<LayoutElem>((int)n);
            var attributes = new HashSet<byte>();
            for (var i = 0; i < n; i++)
            {
                var e = body.Slice(o + 4 + 8 * i, 8);
                if (e[2] is 0 || e[2] >= VertexFormats.Length || e[0] >= 32 || !attributes.Add(e[3])) return null;
                if (e[1] % Math.Min(4u, Planning.ExactLayouts.FormatSize(VertexFormats[e[2]])) != 0) return null; // min(4, size): stricter than D3D12's, no shipped declaration seen breaks it
                var instanced = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]) != 0 ? 1u : 0;
                layout.Add(new LayoutElem("ATTRIBUTE", e[3], VertexFormats[e[2]], e[1], e[0], instanced, instanced));
            }
            return layout;
        }
        return null;
    }

    /// <summary>The body's non-zero hashes are the TOC's, in its order.</summary>
    static bool SameShaders(ReadOnlySpan<byte> hashes, string[] shaders, int width)
    {
        var j = 0;
        for (var i = 0; i < 5; i++)
        {
            var h = hashes.Slice(i * width, width);
            if (h.IndexOfAnyExcept((byte)0) < 0) continue;
            if (j == shaders.Length || Convert.ToHexStringLower(h).PadRight(40, '0') != shaders[j]) return false;
            j++;
        }
        return j > 0 && j == shaders.Length;
    }

    /// <summary>The file version a pipeline cache starts with; null when it isn't one.</summary>
    public static uint? Version(ReadOnlySpan<byte> b) => b.Length >= 12 && U64(b, 0) == Magic ? U32(b, 8) : null;

    static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
}
