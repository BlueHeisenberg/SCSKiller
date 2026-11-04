using System.Buffers.Binary;
using System.Security.Cryptography;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;
using ZstdSharp;

namespace SCSKiller.Core.Aska;

/// <summary>
/// ASKA's DX11 boot shader libraries. The tested 11hA formats are 0x0000008f (The Last Hope, halfword LZ)
/// and 0x000d009d (The Divine Force, dictionary-backed zstd). Disc allocations bound both the compressed library and
/// raw boot resources. No exe names or store ids are needed; other library formats are reported as unsupported.
/// </summary>
public sealed class AskaReader : IEngineReader
{
    public const string Family = "ASKA";
    const string Platform = "PCD3D_SM5";
    const uint Legacy = 0x0000008f;
    const uint Zstd = 0x000d009d;
    const int HeaderSize = 36;
    const int MaxBlock = 64 << 20;

    static readonly string[] Archives = ["aska0000.bin", "0000.bin"];

    sealed record Archive(string Path, AskaDisc.Allocation[] Boot);

    static Archive? Find(Game game)
    {
        foreach (var name in Archives)
        {
            var path = GameFiles.Find(game.InstallDir, name);
            if (!File.Exists(path))
            {
                continue;
            }

            using var f = File.OpenRead(path);
            if (AskaDisc.Boot(f) is { } boot)
            {
                return new(path, boot);
            }
        }

        return null;
    }

    public EngineInfo? Detect(Game game)
    {
        if (Find(game) is not { } archive)
        {
            return null;
        }

        using var f = File.OpenRead(archive.Path);
        var header = Header(f, archive.Boot[1]);
        if (header == null)
        {
            return new(Family, "unknown", null, "D3D11", false, "unsupported ASKA shader library header");
        }

        var format = U(header, 8);
        // These identify library formats, not a known engine release version.
        return new(
            Family,
            $"library {format:x8}",
            null,
            "D3D11",
            false,
            format is Legacy or Zstd ? null : $"unsupported ASKA shader library format 0x{format:x8}");
    }

    public string IndexStamp(Game game) => Find(game) is { } archive
        ? ContentStamps.Of(Path.GetFileName(archive.Path), archive.Path)
        : "";

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) =>
        Index(game, engine, log, null, ct);

    public ShaderIndex Index(
        Game game,
        EngineInfo engine,
        IProgress<string>? log,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        var shaders = new Dictionary<string, ShaderInfo>();
        Walk(
            game,
            (sha, bytes) =>
            {
                if (shaders.ContainsKey(sha))
                {
                    return;
                }

                var info = ShaderContainer.Parse(bytes, sha, new(0, 0, 0, 0))
                    ?? throw new InvalidDataException($"ASKA shader {sha} has an unsupported program");
                shaders.Add(sha, info with
                {
                    Counts = CarvedReader.Counts(info.Bindings)
                });
            },
            progress,
            ct);

        if (shaders.Count == 0)
        {
            throw new InvalidDataException("no DXBC shaders in the ASKA library");
        }

        log?.Report($"ASKA: {shaders.Count} shaders ({string.Join(", ",
            shaders.Values
                .GroupBy(s => s.Stage)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} {g.Key}"))})");

        // Hash the actual shader set: store copies with identical content share the same index identity.
        var hash = CarvedReader.Sha1Hex(string.Join('\n', shaders.Keys.Order(StringComparer.Ordinal)));
        return new(
            hash,
            [Platform],
            shaders,
            [new ShaderMap(hash, "ASKA boot shaders", Platform, shaders.Keys.ToList())]);
    }

    public void ReadShaders(
        Game game,
        EngineInfo engine,
        IReadOnlySet<string> sha1s,
        Action<string, byte[]> sink,
        CancellationToken ct)
    {
        if (sha1s.Count == 0)
        {
            return;
        }

        var left = new HashSet<string>(sha1s);
        Walk(
            game,
            (sha, bytes) =>
            {
                if (left.Remove(sha))
                {
                    sink(sha, bytes);
                }
            },
            null,
            ct);
    }

    static byte[]? Header(Stream f, AskaDisc.Allocation library)
    {
        if (library.Size < HeaderSize)
        {
            return null;
        }

        f.Position = library.Offset;
        var header = new byte[HeaderSize];
        f.ReadExactly(header);

        return header.AsSpan(0, 4).SequenceEqual("11hA"u8) && U(header, 4) == 0
            && header.AsSpan(20).IndexOfAnyExcept((byte)0) < 0
            ? header
            : null;
    }

    static void Walk(
        Game game,
        Action<string, byte[]> sink,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var archive = Find(game) ?? throw new InvalidDataException("no ASKA boot shader library");
        using var f = File.OpenRead(archive.Path);
        var library = archive.Boot[1];
        var header = Header(f, library) ?? throw new InvalidDataException("unsupported ASKA shader library header");
        var format = U(header, 8);
        if (format is not (Legacy or Zstd))
        {
            throw new InvalidDataException($"unsupported ASKA shader library format 0x{format:x8}");
        }

        var count = U(header, 12);
        var dictionaryKiB = U(header, 16);
        if (count is 0 or > 1_000_000 || (format == Legacy ? dictionaryKiB != 0 : dictionaryKiB is 0 or > 16 * 1024))
        {
            throw new InvalidDataException("ASKA library has invalid dictionary or record count");
        }

        var dictionarySize = format == Legacy ? 8192 : (int)dictionaryKiB * 1024;
        var end = library.Offset + library.Size;
        if (dictionarySize > end - f.Position)
        {
            throw new InvalidDataException("truncated ASKA dictionary");
        }

        var dictionary = new byte[dictionarySize];
        f.ReadExactly(dictionary);
        using var zstd = format == Zstd ? new Decompressor() : null;
        if (zstd != null)
        {
            if (U(dictionary, 0) != 0xec30a437)
            {
                throw new InvalidDataException("ASKA zstd dictionary has no header");
            }

            zstd.LoadDictionary(dictionary);
        }

        var sizes = new byte[16];
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (end - f.Position < sizes.Length)
            {
                throw new InvalidDataException($"truncated ASKA record {i}");
            }

            f.ReadExactly(sizes);
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(sizes.AsSpan(2));
            var packed = U(sizes, 4);
            var unpacked = U(sizes, 8);
            var flags = sizes[14] & 3;
            if (offset < sizes.Length
                || packed <= offset
                || packed > MaxBlock
                || unpacked <= offset
                || unpacked > MaxBlock
                || packed - sizes.Length > end - f.Position
                || flags != (format == Legacy ? 3 : 1))
            {
                throw new InvalidDataException($"ASKA record {i} has invalid sizes or compression flags");
            }

            var record = new byte[(int)packed];
            sizes.CopyTo(record, 0);
            f.ReadExactly(record.AsSpan(sizes.Length));
            var bytes = new byte[(int)unpacked - offset];
            if (zstd != null)
            {
                if (zstd.Unwrap(record.AsSpan(offset), bytes) != bytes.Length)
                {
                    throw new InvalidDataException($"ASKA record {i} doesn't decode to its declared size");
                }
            }
            else
            {
                AskaLz.Decode(record.AsSpan(offset), dictionary, bytes);
            }

            if (Emit(bytes, sink) == 0)
            {
                throw new InvalidDataException($"ASKA record {i} has no valid DXBC shader");
            }

            if (i % 128 == 0)
            {
                progress?.Report((i, (int)count));
            }
        }

        // Allocation slots 2 and 4 contain raw boot shaders and utility programs. Never carve the dictionary's
        // training fragments, the allocation table, or the asset packages after these boot resources.
        foreach (var slot in new[] { 2, 4 })
        {
            ct.ThrowIfCancellationRequested();
            var resource = archive.Boot[slot];
            if (resource.Size > MaxBlock)
            {
                throw new InvalidDataException("ASKA boot shader resource is too large");
            }

            var bytes = new byte[resource.Size];
            f.Position = resource.Offset;
            f.ReadExactly(bytes);
            Emit(bytes, sink);
        }

        progress?.Report(((int)count, (int)count));
    }

    static int Emit(byte[] bytes, Action<string, byte[]> sink)
    {
        var count = 0;
        foreach (var (_, shader) in Dxbc.Containers(bytes))
        {
            sink(Convert.ToHexStringLower(SHA1.HashData(shader)), shader);
            count++;
        }

        return count;
    }

    static uint U(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
}
