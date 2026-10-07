using System.Buffers.Binary;

namespace SCSKiller.Core.Unreal;

/// <summary>Unreal's shipped pipeline cache (Content/PipelineCaches/&lt;Platform&gt;/&lt;Project&gt;_&lt;ShaderPlatform&gt;.stable.upipelinecache),
/// read from its table of contents: each PSO's type and the library hashes (FSHAHash) of its shaders, and from a graphics
/// PSO's body its vertex declaration. Layout from Epic's PipelineFileCache.cpp (FPipelineCacheFileFormatHeader,
/// FPipelineCacheFileFormatTOC, FPipelineCacheFileFormatPSOMetaData, FPipelineCacheFileFormatPSO::GraphicsDescriptor).</summary>
public static class StablePipelineCache
{
    public enum PsoType : uint { Compute = 0, Graphics = 1, RayTracing = 2 }

    /// <summary>One FVertexElement of a graphics PSO's vertex declaration (RHIResources.h): the vertex buffer slot it streams
    /// from, its byte offset in that stream, its EVertexElementType (<see cref="Element.Type"/>), its ATTRIBUTE semantic
    /// index, the stream's stride and whether it steps per instance.</summary>
    public readonly record struct Element(byte Stream, byte Offset, byte Type, byte Attribute, ushort Stride, bool Instance);

    /// <summary>A PSO of the cache. <paramref name="Layout"/>: a graphics PSO's vertex declaration (empty: none bound); null
    /// for the other types, and for graphics ones when the file's bodies aren't the shape this reads.</summary>
    public sealed record Pso(uint Key, PsoType Type, string[] Shaders, IReadOnlyList<Element>? Layout = null);

    const ulong Magic = 0x5049504543414348, TocMagic = 0x544F435354415232, TocMagic17 = 0x544F435354415254, EofMagic = 0x454F462D4D41524B; // PIPECACH, TOCSTAR2, TOCSTART, EOF-MARK

    /// <summary>File versions 22 (LastUsedTime) to 28 (AddingDepthBounds) share this header and TOC layout; between them only
    /// the PSO bodies change. Version 17 (Subpass, UE 4.25) has the TOCSTART table: no guid shared by every entry, no last
    /// used time per entry. A fork may write version 22 with no last used time either and an int32 before the end marker
    /// (Kuro's: Wuthering Waves): read as such when the stock layout doesn't end at the marker. Null: not such a file.</summary>
    public static List<Pso>? Read(ReadOnlySpan<byte> b)
    {
        if (b.Length < 57 || U64(b, 0) != Magic || U32(b, 8) is not (17 or (>= 22 and <= 28)) || U64(b, b.Length - 8) != EofMagic) return null;
        var v17 = U32(b, 8) == 17;
        return Toc(b, v17, lastUsed: !v17, tail: 8) ?? (v17 ? null : Toc(b, false, lastUsed: false, tail: 12));
    }

    /// <summary>Null: not this TOC layout (the fork's misreads a guid as a body size: caught here, so the other layout gets its try).</summary>
    static List<Pso>? Toc(ReadOnlySpan<byte> b, bool v17, bool lastUsed, int tail)
    {
        try { return TocCore(b, v17, lastUsed, tail); }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException) { return null; }
    }

    static List<Pso>? TocCore(ReadOnlySpan<byte> b, bool v17, bool lastUsed, int tail)
    {
        var o = checked((int)U64(b, 33)); // after magic, version, game version, u8 platform, guid
        if (U64(b, o) != (v17 ? TocMagic17 : TocMagic)) return null;
        o += 8;
        if (!v17) o += b[o] != 0 ? 17 : 1; // every entry's guid, once
        o += 4; // sort order
        var n = BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
        o += 4;
        if (n < 0 || n > b.Length / 86) return null; // an entry is 86 bytes at least
        var psos = new List<Pso>(n);
        var bodies = new List<(int Offset, int Size)>(n);
        for (var i = 0; i < n; i++)
        {
            var key = U32(b, o);
            var body = checked((int)U64(b, o + 4));
            var size = checked((int)U64(b, o + 12));
            o += 4 + 16 + 16 + 36; // key, file offset + size, guid, stats
            var k = BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
            o += 4;
            if (k is < 0 or > 8) return null; // a PSO has at most five stages (Kuro's bodies list seven, its TOC entries still the bound ones)
            var shaders = new string[k];
            for (var j = 0; j < k; j++) shaders[j] = Convert.ToHexStringLower(b.Slice(o + 20 * j, 20));
            o += 20 * k + 8 + 2 + (lastUsed ? 8 : 0); // shaders, usage mask, engine flags, last used time
            if (body < 0 || size < 4 || body > b.Length - size) return null;
            psos.Add(new Pso(key, (PsoType)U32(b, body), shaders));
            bodies.Add((body, size));
        }
        if (o != b.Length - tail || tail == 12 && U32(b, o) != 0) return null;
        var hashes = HashCount(b, psos, bodies);
        if (hashes == 0) return psos;
        for (var i = 0; i < psos.Count; i++)
            if (psos[i].Type == PsoType.Graphics) psos[i] = psos[i] with { Layout = Elements(b, bodies[i].Offset + 4 + 20 * hashes) };
        return psos;
    }

    /// <summary>How many FSHAHashes a graphics body opens with before its vertex declaration: stock UE's five (VS, PS, GS,
    /// HS, DS; UE 5: VS, PS, GS, MS, AS; version 17's bodies open the same way), or seven for a fork that kept 4's stages and
    /// added 5's (Kuro's). Decided per file, without knowing the rest of the body: with the right count every body's size
    /// less its elements' is the one fixed size; with the wrong one the "count" is hash bytes and the residue scatters. The
    /// bodies must show two element counts at least: all alike (or one body) fits either reading. 0: no count fits (bodies
    /// of another shape, or too few to tell), no declarations read.</summary>
    static int HashCount(ReadOnlySpan<byte> b, List<Pso> psos, List<(int Offset, int Size)> bodies)
    {
        foreach (var hashes in new[] { 5, 7 })
        {
            int? fixedSize = null;
            var counts = new HashSet<int>();
            var fits = true;
            for (var i = 0; i < psos.Count && fits; i++)
            {
                if (psos[i].Type != PsoType.Graphics) continue;
                var (off, size) = bodies[i];
                var at = 4 + 20 * hashes;
                if (size < at + 4) { fits = false; break; }
                var count = BinaryPrimitives.ReadInt32LittleEndian(b[(off + at)..]);
                if (count is < 0 or > 17 || size < at + 4 + 8 * count) { fits = false; break; } // MaxVertexElementCount
                counts.Add(count);
                var rest = size - 8 * count;
                if (fixedSize is { } f && f != rest) fits = false;
                fixedSize = rest;
            }
            if (fits && counts.Count >= 2) return hashes;
        }
        return 0;
    }

    /// <summary>The D3D12 input element UE's D3D12 RHI makes of a vertex element (FD3D12VertexDeclarationKey): semantic
    /// ATTRIBUTE&lt;attribute&gt;, <see cref="DxgiFormat"/>, the stream as the input slot, per-instance elements at step rate 1.
    /// Null: a type the RHI has no format for (the layout can't be created).</summary>
    public static Planning.PsoDb.LayoutElem? InputElement(Element e) =>
        DxgiFormat(e.Type) is var f and not 0 ? new("ATTRIBUTE", e.Attribute, f, e.Offset, e.Stream, e.Instance ? 1u : 0, e.Instance ? 1u : 0) : null;

    /// <summary>A PSO's layout as D3D12 input elements; null when it has none or an element has no format.</summary>
    public static List<Planning.PsoDb.LayoutElem>? InputLayout(Pso p)
    {
        if (p.Layout == null) return null;
        var list = new List<Planning.PsoDb.LayoutElem>(p.Layout.Count);
        foreach (var e in p.Layout) if (InputElement(e) is { } x) list.Add(x); else return null;
        return list;
    }

    static Element[] Elements(ReadOnlySpan<byte> b, int o)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
        var elements = new Element[count];
        for (var i = 0; i < count; i++)
        {
            var p = o + 4 + 8 * i; // StreamIndex, Offset, Type, AttributeIndex (u8 each), Stride (u16), bUseInstanceIndex (u16)
            elements[i] = new Element(b[p], b[p + 1], b[p + 2], b[p + 3], BinaryPrimitives.ReadUInt16LittleEndian(b[(p + 4)..]), BinaryPrimitives.ReadUInt16LittleEndian(b[(p + 6)..]) != 0);
        }
        return elements;
    }

    /// <summary>The DXGI_FORMAT UE's D3D12 RHI gives an EVertexElementType (D3D12VertexDeclaration.cpp, TranslateElementTypeToFormat);
    /// 0 (UNKNOWN) for VET_None and unknown values.</summary>
    public static uint DxgiFormat(byte type) => type switch
    {
        1 => 41,  // VET_Float1: R32_FLOAT
        2 => 16,  // VET_Float2: R32G32_FLOAT
        3 => 6,   // VET_Float3: R32G32B32_FLOAT
        4 => 2,   // VET_Float4: R32G32B32A32_FLOAT
        5 => 31,  // VET_PackedNormal: R8G8B8A8_SNORM
        6 => 30,  // VET_UByte4: R8G8B8A8_UINT
        7 => 28,  // VET_UByte4N: R8G8B8A8_UNORM
        8 => 87,  // VET_Color: B8G8R8A8_UNORM
        9 => 38,  // VET_Short2: R16G16_SINT
        10 => 14, // VET_Short4: R16G16B16A16_SINT
        11 => 37, // VET_Short2N: R16G16_SNORM
        12 => 34, // VET_Half2: R16G16_FLOAT
        13 => 10, // VET_Half4: R16G16B16A16_FLOAT
        14 => 13, // VET_Short4N: R16G16B16A16_SNORM
        15 => 36, // VET_UShort2: R16G16_UINT
        16 => 12, // VET_UShort4: R16G16B16A16_UINT
        17 => 35, // VET_UShort2N: R16G16_UNORM
        18 => 11, // VET_UShort4N: R16G16B16A16_UNORM
        19 => 24, // VET_URGB10A2N: R10G10B10A2_UNORM
        20 => 42, // VET_UInt: R32_UINT
        _ => 0,
    };

    static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
}
