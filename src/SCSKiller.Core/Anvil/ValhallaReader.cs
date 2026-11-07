using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.Anvil;

/// <summary>Valhalla's loose SBIN shader table and Oodle-compressed DX12 Forge resources. Recognized Forge variant tables
/// supply exact stage sets and select root profiles by their layout flag.</summary>
public sealed class ValhallaReader : IEngineReader
{
    public const string Family = "Anvil (Valhalla)";
    const string Exe = "ACValhalla.exe", ShaderFile = "ShaderDataDX12.bin", Platform = "D3D12";
    const string SupportedSbin = "57e6eafdcae9acad8cd4cc206c1aafb8f2242dceba4226c098fe876a1ce61bb0";
    const int SbinHeader = 12, SbinRecord = 28;
    readonly record struct Location(string Path, ValhallaForge.Entry? Entry, long Offset, int Size);
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Location>> located = new();

    public int ReaderVersion(EngineInfo engine) => 5;

    public EngineInfo? Detect(Game game)
    {
        if (!Path.GetFileName(game.ExePath).Equals(Exe, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var file = Open(GameFiles.Find(game.InstallDir, ShaderFile));
            if (ReadSbinHeader(file) == null) return null;
            var version = file.Length == 70_000_656 && Matches(file, SupportedSbin) ? "DXBC" + CarvedReader.EmbeddedRootSignatures : "DXBC";
            return new EngineInfo(Family, version, null, Platform, false, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var shaders = new Dictionary<string, ShaderInfo>();
        var locations = new Dictionary<string, Location>();
        var maps = new List<ShaderMap>();
        var stamp = new List<string>();
        var stageSets = new HashSet<(string Vertex, string? Pixel, string? Root)>();
        var roots = new Dictionary<string, byte[]>();
        var compute = new Dictionary<string, HashSet<ValhallaRootProfiles.Profile>>();
        int looseCount = 0, forgeCount = 0, forgeFiles = 0, variants = 0, failures = 0;
        string? looseStamp = null;
        var path = GameFiles.Find(game.InstallDir, ShaderFile);
        try
        {
            using var file = Open(path);
            var pool = new HashSet<string>();
            var supported = file.Length == 70_000_656 && Matches(file, SupportedSbin);
            var count = ReadSbinHeader(file) ?? throw new InvalidDataException("invalid SBIN header");
            Span<byte> record = stackalloc byte[SbinRecord];
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var start = file.Position;
                file.ReadExactly(record);
                uint headerSize = U32(record), size = U32(record[4..]);
                if (headerSize < SbinRecord || headerSize - SbinRecord != U32(record[24..]) ||
                    size > Dxbc.MaxSize || start + headerSize + size > file.Length)
                    throw new InvalidDataException($"invalid SBIN entry {i}");
                string? source = null;
                if (supported && headerSize - SbinRecord is > 0 and <= 4096)
                {
                    var description = new byte[headerSize - SbinRecord];
                    file.ReadExactly(description);
                    source = ComputeSource(description);
                }
                file.Position = start + headerSize;
                var offset = file.Position;
                var code = new byte[size];
                file.ReadExactly(code);
                if (!Dxbc.Valid(code)) throw new InvalidDataException($"invalid DXBC in SBIN entry {i}");
                var info = Add(pool, code, new(path, null, offset, code.Length));
                if (info?.Stage == Stage.Compute && source != null && ValhallaRootProfiles.Compute.TryGetValue(source, out var profile))
                {
                    if (!compute.TryGetValue(info.Sha1, out var candidates)) compute.Add(info.Sha1, candidates = []);
                    candidates.Add(profile);
                }
                looseCount++;
            }
            if (file.Position != file.Length) throw new InvalidDataException("unexpected data after the SBIN entries");
            foreach (var (sha, candidates) in compute)
            {
                if (candidates.Count != 1 || !candidates.First().Covers(shaders[sha])) continue;
                var root = Use(candidates.First());
                maps.Add(new(CarvedReader.Sha1Hex(sha), ShaderFile, Platform, [sha], IsPipeline: true, RootSignature: root));
                pool.Remove(sha);
            }
            Map(ShaderFile, pool);
            looseStamp = ContentStamps.Of(ShaderFile, path);
            stamp.Add(looseStamp);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log?.Report($"{ShaderFile}: {e.Message}");
            shaders.Clear();
            locations.Clear();
            looseCount = 0;
        }

        foreach (var archive in Archives(game))
        {
            ct.ThrowIfCancellationRequested();
            var rel = GameFiles.Rel(game.InstallDir, archive);
            try
            {
                using var file = Open(archive);
                var entries = ValhallaForge.Entries(file);
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var data = ValhallaForge.Read(file, entry, ct);
                        if (data == null) continue;
                        var pool = new HashSet<string>();
                        var containers = new Dictionary<int, (int Size, ShaderInfo Shader)>();
                        foreach (var (offset, code) in Dxbc.Containers(data))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (Add(pool, code, new(archive, entry, offset, code.Length)) is { } info)
                                containers.Add(offset, (code.Length, info));
                            forgeCount++;
                        }
                        var name = $"{rel}:{entry.Id:x}";
                        if (ValhallaStages.Read(data, containers, ct) is { } sets)
                        {
                            variants += sets.Count;
                            foreach (var set in sets)
                            {
                                string[] stages = set.Pixel is { } ps ? [set.Vertex, ps] : [set.Vertex];
                                var profile = set.Bindless ? ValhallaRootProfiles.ForgeBindless : ValhallaRootProfiles.ForgeRegular;
                                var root = stages.All(h => profile.Covers(shaders[h])) ? Use(profile) : null;
                                if (stageSets.Add((set.Vertex, set.Pixel, root)))
                                    maps.Add(new(CarvedReader.Sha1Hex(string.Join(',', stages) + "|" + root), name, Platform, stages,
                                        IsPipeline: true, RootSignature: root));
                            }
                        }
                        else Map(name, pool);
                    }
                    catch (InvalidDataException e)
                    {
                        if (failures++ == 0) log?.Report($"{rel}: {e.Message}");
                    }
                }
                stamp.Add(ContentStamps.Of(rel, archive));
                forgeFiles++;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                log?.Report($"{rel}: {e.Message}");
                failures++;
            }
        }
        located[game.Id] = locations;
        log?.Report($"Anvil: {looseCount:N0} SBIN and {forgeCount:N0} Forge DXBC records in {forgeFiles:N0} archives; "
            + $"{shaders.Count:N0} distinct indexed shaders, {stageSets.Count:N0} stage sets from {variants:N0} variants, {failures:N0} read failures");
        var contentHash = shaders.Count == 0 ? ShaderIndex.EmptyContentHash : CarvedReader.Sha1Hex(string.Concat(stamp));
        // Before the Anvil reader, the raw carver indexed the loose table under this content stamp.
        var legacy = looseStamp == null ? null : CarvedReader.Sha1Hex(looseStamp);
        return new ShaderIndex(contentHash, [Platform], shaders, maps, roots,
            LegacyHashes: legacy != null && legacy != contentHash ? [legacy] : null);

        string Use(ValhallaRootProfiles.Profile profile)
        {
            roots.TryAdd(profile.Hash, profile.Blob);
            return profile.Hash;
        }

        ShaderInfo? Add(HashSet<string> pool, byte[] code, Location at)
        {
            var sha = Hash(code);
            if (!shaders.TryGetValue(sha, out var info))
            {
                try { info = ShaderContainer.Parse(code, sha, new(0, 0, 0, 0)); }
                catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { return null; }
                if (info == null) return null;
                info = info with { Counts = CarvedReader.Counts(info.Bindings) };
                shaders.Add(sha, info);
                locations.Add(sha, at);
            }
            pool.Add(sha);
            return info;
        }

        void Map(string name, HashSet<string> pool)
        {
            if (pool.Count > 0) maps.Add(new(CarvedReader.Sha1Hex(name), name, Platform, [.. pool]));
        }
    }

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var locations))
        {
            Index(game, engine, null, ct);
            locations = located[game.Id];
        }
        foreach (var profile in ValhallaRootProfiles.All)
            if (sha1s.Contains(profile.Hash)) sink(profile.Hash, profile.Blob);
        foreach (var byFile in sha1s.Where(locations.ContainsKey).GroupBy(sha => locations[sha].Path))
        {
            ct.ThrowIfCancellationRequested();
            using var file = Open(byFile.Key);
            foreach (var byEntry in byFile.GroupBy(sha => locations[sha].Entry))
            {
                ct.ThrowIfCancellationRequested();
                var data = byEntry.Key is { } entry ? ValhallaForge.Read(file, entry, ct) : null;
                if (byEntry.Key != null && data == null) continue;
                foreach (var sha in byEntry)
                {
                    ct.ThrowIfCancellationRequested();
                    var at = locations[sha];
                    byte[] code;
                    if (data != null)
                    {
                        if (at.Offset + at.Size > data.Length) continue;
                        code = data.AsSpan((int)at.Offset, at.Size).ToArray();
                    }
                    else
                    {
                        if (at.Offset + at.Size > file.Length) continue;
                        file.Position = at.Offset;
                        code = new byte[at.Size];
                        file.ReadExactly(code);
                    }
                    if (Hash(code) == sha) sink(sha, code);
                }
            }
        }
    }

    static IEnumerable<string> Archives(Game game) => GameFiles.EnumerateFiles(game.InstallDir, "*.forge",
        new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
        .Where(path => Path.GetFileNameWithoutExtension(path).Contains("_DX12", StringComparison.OrdinalIgnoreCase))
        .Order(GameFiles.PathOrder);

    static uint? ReadSbinHeader(FileStream file)
    {
        Span<byte> header = stackalloc byte[SbinHeader];
        if (file.ReadAtLeast(header, SbinHeader, throwOnEndOfStream: false) != SbinHeader || !header.StartsWith("SBIN"u8)) return null;
        var offset = U32(header[4..]);
        var count = U32(header[8..]);
        if (offset < SbinHeader || offset > file.Length || count == 0 || count > (file.Length - offset) / SbinRecord) return null;
        file.Position = offset;
        return count;
    }

    static string? ComputeSource(Span<byte> description)
    {
        for (var i = 0; i < description.Length; i++) description[i] ^= "SBIN"u8[i % 4];
        if (!description.StartsWith("CS:"u8)) return null;
        var name = description[3..];
        var end = name.IndexOf((byte)':');
        return end > 0 ? Encoding.ASCII.GetString(name[..end]) : null;
    }

    static bool Matches(FileStream file, string sha256)
    {
        file.Position = 0;
        var matches = Convert.ToHexStringLower(SHA256.HashData(file)) == sha256;
        file.Position = 0;
        return matches;
    }

    internal static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));
    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
