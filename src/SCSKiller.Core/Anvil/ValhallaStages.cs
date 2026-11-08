using System.Buffers.Binary;
using static SCSKiller.Core.Anvil.ValhallaReader;

namespace SCSKiller.Core.Anvil;

/// <summary>The resource's graphics variants name a VS and an optional PS in the same bytecode table. The rows describe
/// stage sets, not D3D12 PSOs that the warmer can replay: the root signature and fixed-function state are still needed.</summary>
internal static class ValhallaStages
{
    static ReadOnlySpan<byte> VariantTable => [0xF8, 0xFB, 0, 0, 0, 0, 0xD6, 0xC5, 0x8A, 0x64];
    const uint VariantType = 0x41745408, ShaderType = 0xDF0956E6;
    const int VariantSize = 48, ShaderSize = 28;

    internal static List<(string Vertex, string? Pixel, bool Bindless)>? Read(ReadOnlySpan<byte> resource,
        IReadOnlyDictionary<int, (int Size, ShaderInfo Shader)> containers, CancellationToken ct)
    {
        if (resource.Length < 4 || U32(resource) != 0x7AF98A66) return null;
        // The header is preceded by variable-sized resource records and inline shader copies.
        var marker = resource.IndexOf(VariantTable);
        if (marker < 2 || resource.Length - marker < 22) return null;
        var firstVariant = marker + 22;
        var variants = U32(resource[(firstVariant - 4)..]);
        if (variants == 0 || variants > (resource.Length - firstVariant - 4) / VariantSize) return null;
        var shaderCountAt = firstVariant + (int)variants * VariantSize;
        var shaderCount = U32(resource[shaderCountAt..]);
        var firstShader = shaderCountAt + 4;
        if (shaderCount == 0 || shaderCount > containers.Count || shaderCount > (resource.Length - firstShader - 12) / ShaderSize)
            return null;
        var blobAt = firstShader + (int)shaderCount * ShaderSize + 12;
        var blobSize = U32(resource[(blobAt - 4)..]);
        if (blobSize > resource.Length - blobAt) return null;
        var shaders = new ShaderInfo[shaderCount];
        for (var i = 0; i < shaders.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = resource[(firstShader + i * ShaderSize)..];
            var offset = U32(row[16..]);
            var size = U32(row[20..]);
            if (!IsRow(row, ShaderType) || offset > blobSize || size > blobSize - offset ||
                !containers.TryGetValue(blobAt + (int)offset, out var c) || c.Size != size)
                return null;
            shaders[i] = c.Shader;
        }
        var sets = new List<(string Vertex, string? Pixel, bool Bindless)>();
        for (var i = 0; i < variants; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = resource[(firstVariant + i * VariantSize)..];
            var v = U32(row[20..]);
            var p = U32(row[24..]);
            if (!IsRow(row, VariantType) || v >= shaders.Length || shaders[v].Stage != Stage.Vertex ||
                p != uint.MaxValue && (p >= shaders.Length || shaders[p].Stage != Stage.Pixel))
                return null;
            var flags = BinaryPrimitives.ReadUInt64LittleEndian(row[12..]);
            sets.Add((shaders[v].Sha1, p == uint.MaxValue ? null : shaders[p].Sha1, (flags & (1ul << 12)) != 0));
        }
        return sets;
    }

    static bool IsRow(ReadOnlySpan<byte> row, uint type) =>
        row.Slice(2, 6).SequenceEqual(VariantTable[..6]) && U32(row[8..]) == type;
}
