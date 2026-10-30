using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.BlackSpace;

/// <summary>Pearl Abyss' BlackSpace engine (Crimson Desert), by its archive groups (<see cref="Pamt"/>, meta\0.papgt beside
/// them). The D3D12 shaders are the <c>shadercache__/d3d12/windows/*.padxil</c> entries of one group: a 36-byte header
/// ("PASC", u32 version, u32 size, u32, u32 header size, 16-byte hash) then one DXIL container. A name is
/// <c>&lt;pass&gt;_&lt;x&gt;_&lt;stage&gt;_&lt;entry&gt;_&lt;x&gt;_&lt;perm&gt;_&lt;perm&gt;</c>, hex words, the first the
/// <see cref="PazCrypto.Hash"/> of its render pass's name as the exe spells it. Each render pass has its root signature in
/// <c>&lt;pass lowercase&gt;.pars</c> beside them, encrypted (an RTS0-only container): with the game's key, which the user
/// gives (<see cref="BlackSpaceKeys"/>), a shader gets the one of its passes, none when they differ or don't decrypt to
/// one. Pass names come from the exe's strings, else from the rules' table (<see cref="PlanRule.PassNames"/>). The shaders of a
/// pass are pooled with the pass's root signature; each compute shader is one IsPipeline map per root signature, on its
/// [WaveSize] platform (<see cref="CarvedReader.LanePlatform"/>).
/// EngineInfo: Family "BlackSpace", Version "padxil+RTS0", or "padxil" without a key that opens them (a recording first).</summary>
/// <param name="dataDir">the app's data folder: the key the user gives is kept there (local only)</param>
public sealed class BlackSpaceReader(string dataDir) : IEngineReader
{
    public const string Family = "BlackSpace", Format = "padxil";
    public const string ShaderDir = "shadercache__/d3d12/windows";
    const int IndexVersion = 1, HeaderSize = 36;

    public sealed record Loc(string Paz, Pamt.Entry Entry, byte KeyByte);

    /// <summary>Where each container is and the root signatures, per game id, from the last Index. ponytail: in memory only,
    /// like the carver's; ReadShaders re-indexes when it runs without an Index in the same process.</summary>
    readonly ConcurrentDictionary<string, (IReadOnlyDictionary<string, Loc> Shaders, IReadOnlyDictionary<string, byte[]> Roots)> located = new();

    readonly BlackSpaceKeys keys = new(dataDir);

    /// <summary>Without a key that opens the root signatures: the game page asks for it.</summary>
    public static bool NeedsKey(EngineInfo e) => e is { Family: Family, Version: Format };

    public int ReaderVersion(EngineInfo engine) => 2;

    /// <summary>A key the user gives: stored when it decrypts one of the game's root signatures.</summary>
    public bool SetKey(Game game, string key) => keys.Set(game, key, k => Opens(game, k));

    bool Opens(Game game, byte[] key)
    {
        if (Root(game) is not { } root || ShaderPamt(root) is not { } path) return false;
        Pamt pamt;
        try { pamt = Pamt.Read(path); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
        return pamt.Files.Where(IsKeyedPars).Any(f => RootSignature(Path.GetDirectoryName(path)!, f, key, pamt.KeyByte) != null);
    }

    /// <summary>The install's root (meta\0.papgt), the install folder or the one above the exe's.</summary>
    static string? Root(Game game) =>
        new[] { game.InstallDir, Path.GetDirectoryName(Path.GetDirectoryName(game.ExePath)) }
            .FirstOrDefault(d => d != null && File.Exists(Path.Combine(d, "meta", "0.papgt")));

    /// <summary>The index of the group holding the shader cache; null when no group has it.</summary>
    static string? ShaderPamt(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { IgnoreInaccessible = true }).Order(StringComparer.Ordinal))
        {
            var pamt = Path.Combine(dir, "0.pamt");
            if (Path.GetFileName(dir) is not { Length: 4 } n || !n.All(char.IsAsciiDigit) || !File.Exists(pamt)) continue;
            try { if (Pamt.FolderNames(pamt).Contains(ShaderDir)) return pamt; }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public string DetectStamp(Game game, EngineInfo? engine) => Root(game) == null ? "" : "paz:" + keys.KeyStamp(game);

    public EngineInfo? Detect(Game game)
    {
        if (Root(game) is not { } root) return null;
        if (ShaderPamt(root) is not { } path) return new EngineInfo(Family, "-", null, "D3D12", false, "no D3D12 shader cache in its archives");
        Pamt pamt;
        try { pamt = Pamt.Read(path); }
        catch (InvalidDataException e) { return new EngineInfo(Family, "-", null, "D3D12", false, $"its shader archive doesn't open ({e.Message})"); }
        var words = keys.Stored(game);
        var roots = words != null && pamt.Files.Where(IsKeyedPars).Any(f => RootSignature(Path.GetDirectoryName(path)!, f, words, pamt.KeyByte) != null);
        return new EngineInfo(Family, Format + (roots ? CarvedReader.EmbeddedRootSignatures : ""), null, "D3D12", false, null);
    }

    static bool IsPars(string p) => p.StartsWith(ShaderDir + "/", StringComparison.Ordinal) && p.EndsWith(".pars", StringComparison.Ordinal);

    /// <summary>A root signature entry the key opens: an unencrypted one would take any key.</summary>
    static bool IsKeyedPars(Pamt.Entry e) => IsPars(e.Path) && e.Encryption == 3;

    static bool IsShader(string p) => p.StartsWith(ShaderDir + "/", StringComparison.Ordinal) && p.EndsWith(".padxil", StringComparison.Ordinal);

    static string PazOf(string group, Pamt.Entry e) => Path.Combine(group, $"{e.Paz}.paz");

    static SafeFileHandle OpenPaz(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>A pass's root signature: the decrypted entry when it's an RTS0-only container the runtime would create.</summary>
    static byte[]? RootSignature(string group, Pamt.Entry e, byte[] keyBase, byte keyByte)
    {
        try
        {
            using var h = OpenPaz(PazOf(group, e));
            var b = Pamt.Open(h, e, keyBase, keyByte, out _);
            return b != null && Dxbc.IsRootSignatureOnly(b) && Dxbc.RootSignatureValid(b) ? b : null;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The container after a .padxil's header; null when it isn't one.</summary>
    internal static byte[]? Container(byte[] padxil)
    {
        if (padxil.Length < HeaderSize || !padxil.AsSpan(0, 4).SequenceEqual("PASC"u8)) return null;
        var at = BinaryPrimitives.ReadUInt32LittleEndian(padxil.AsSpan(16));
        if (at < HeaderSize || at >= padxil.Length) return null;
        var c = padxil[(int)at..];
        return Dxbc.HeaderSize(c) == c.Length && Dxbc.Valid(c) ? c : null;
    }

    /// <summary>The render pass hash a shader's name starts with; null for another name.</summary>
    internal static uint? PassOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var cut = name.IndexOf('_');
        return cut == 8 && uint.TryParse(name.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, null, out var h) ? h : null;
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => Index(game, engine, log, null, ct);

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var root = Root(game) ?? throw new InvalidDataException("not a BlackSpace install: scan the game again");
        var path = ShaderPamt(root) ?? throw new InvalidDataException("no D3D12 shader cache in its archives");
        var group = Path.GetDirectoryName(path)!;
        var pamtBytes = Pamt.Bytes(path);
        var pamt = Pamt.Parse(pamtBytes);
        var words = keys.Stored(game);
        var entries = pamt.Files.Where(f => IsShader(f.Path) || IsPars(f.Path)).OrderBy(f => f.Paz).ThenBy(f => f.Offset).ToList();

        var shaders = new Dictionary<string, ShaderInfo>();
        var locs = new Dictionary<string, Loc>();
        var passes = new Dictionary<uint, HashSet<string>>();
        var passRoots = new Dictionary<string, string>();   // lowercase pass name -> root signature SHA-1
        var lanes = new Dictionary<string, string>();       // shader -> its [WaveSize] platform
        var roots = new Dictionary<string, byte[]>();
        var skipped = new Dictionary<string, int>();
        int containers = 0, bad = 0, badRoots = 0, done = 0;
        var handles = new Dictionary<int, SafeFileHandle>();
        try
        {
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((++done, entries.Count));
                if (!handles.TryGetValue(e.Paz, out var h)) handles[e.Paz] = h = OpenPaz(PazOf(group, e));
                if (IsPars(e.Path))
                {
                    if (words == null) continue;
                    var unread = e.Encryption == 0 ? "a root signature not encrypted" : e.Encryption != 3 ? $"encryption {e.Encryption}"
                        : e.Compression is not (0 or 2) ? $"compression {e.Compression}" : null;
                    if (unread != null) { skipped[unread] = skipped.GetValueOrDefault(unread) + 1; continue; }
                    var b = Pamt.Open(h, e, words, pamt.KeyByte, out _);
                    if (b == null || !Dxbc.IsRootSignatureOnly(b) || !Dxbc.RootSignatureValid(b)) { badRoots++; continue; }
                    var rs = Convert.ToHexStringLower(SHA1.HashData(b));
                    roots[rs] = b;
                    passRoots[Path.GetFileNameWithoutExtension(e.Path)] = rs;
                    continue;
                }
                var raw = Pamt.Open(h, e, words, pamt.KeyByte, out var why);
                if (raw == null) { skipped[why!] = skipped.GetValueOrDefault(why!) + 1; continue; }
                if (Container(raw) is not { } c) { bad++; continue; }
                containers++;
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                if (locs.TryAdd(sha, new Loc(PazOf(group, e), e, pamt.KeyByte)))
                    try
                    {
                        if (ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info)
                        {
                            shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                            if (info.Stage == Stage.Compute && CarvedReader.LanePlatform(Dxbc.WaveLanes(c)) is { } l) lanes[sha] = l;
                        }
                    }
                    catch (Exception x) when (x is ArgumentException or IndexOutOfRangeException) { bad++; }
                if (!shaders.ContainsKey(sha)) continue;
                var pass = PassOf(e.Path) ?? 0;
                if (!passes.TryGetValue(pass, out var set)) passes[pass] = set = [];
                set.Add(sha);
            }
        }
        finally { foreach (var h in handles.Values) h.Dispose(); }
        if (shaders.Count == 0) throw new InvalidDataException($"no shader in {GameFiles.Rel(root, path)}");

        var names = PassNames(game.ExePath, passRoots.Keys.ToHashSet(), passes.Keys.ToHashSet());
        // hashed before the rules add theirs: those follow the rules version, which the plan checks apart
        var exeNames = string.Join(' ', names.OrderBy(n => n.Key).Select(n => $"{n.Key:x8}={n.Value}"));
        var fromRules = RulesNames(RulesFile.Current.PassNamesFor(engine)?.PassNames, names, passRoots.Keys.ToHashSet(), passes.Keys.ToHashSet());
        string? RootOf(uint pass) => names.TryGetValue(pass, out var n) ? passRoots.GetValueOrDefault(n) : null;
        var rootsOf = new Dictionary<string, HashSet<string?>>();
        foreach (var (pass, set) in passes)
            foreach (var sha in set)
                (rootsOf.TryGetValue(sha, out var r) ? r : rootsOf[sha] = []).Add(RootOf(pass));
        foreach (var (sha, r) in rootsOf)
            if (r.Count == 1 && r.Single() is { } only) shaders[sha] = shaders[sha] with { RootSignature = only };

        var maps = Maps(passes, shaders, p => names.GetValueOrDefault(p) ?? p.ToString("x8"), RootOf, h => lanes.GetValueOrDefault(h, CarvedReader.Platform));
        located[game.Id] = (locs, roots);

        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        content.AppendData(Encoding.UTF8.GetBytes($"{Family} {IndexVersion}\n"));
        content.AppendData(pamtBytes);
        // shared, so the same on every PC: what the key and the exe gave, never the key or a file time
        content.AppendData(Encoding.UTF8.GetBytes(string.Join(' ', passRoots.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")) + "\n"));
        content.AppendData(Encoding.UTF8.GetBytes(exeNames + "\n"));

        var withRoot = shaders.Values.Count(s => s.RootSignature != null);
        log?.Report($"{GameFiles.Rel(root, path)}: {containers} shader containers, {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"), {shaders.Values.Count(s => s.InlineRayTracing)} tracing rays inline, {passes.Count} render passes, {names.Count} named{(fromRules > 0 ? $" ({fromRules} by the rules)" : "")}"
            + (words == null ? "; root signatures: none, no key for its .pars files"
                : $"; {roots.Count} root signatures, {withRoot} shaders with theirs" + (badRoots > 0 ? $"; warning: {badRoots} .pars files aren't root signatures (a wrong key?), their passes have none" : ""))
            + string.Concat(skipped.Select(s => $"; warning: {s.Value} entries skipped ({s.Key})"))
            + (bad > 0 ? $"; {bad} unparseable" : "")
            + $" ({sw.Elapsed.TotalSeconds:F1}s)");
        return new ShaderIndex(Convert.ToHexStringLower(content.GetHashAndReset()), [CarvedReader.Platform, .. lanes.Values.Distinct().Order(StringComparer.Ordinal)], shaders, maps, roots);
    }

    /// <summary>One pooled map per render pass of its graphics shaders and libraries, with the pass's root signature, and one
    /// IsPipeline map per compute shader and root signature of the passes it's in, on the shader's platform.</summary>
    internal static List<ShaderMap> Maps(Dictionary<uint, HashSet<string>> passes, IReadOnlyDictionary<string, ShaderInfo> shaders,
        Func<uint, string> name, Func<uint, string?> rootOf, Func<string, string>? platform = null)
    {
        var maps = new List<ShaderMap>();
        var compute = new HashSet<(string, string?)>();
        foreach (var (pass, set) in passes.OrderBy(p => p.Key))
        {
            var lib = name(pass);
            var pool = set.Where(h => shaders[h].Stage != Stage.Compute).Order(StringComparer.Ordinal).ToList();
            if (pool.Count > 0) maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{Family}|{pass:x8}"), lib, CarvedReader.Platform, pool, RootSignature: rootOf(pass)));
            foreach (var cs in set.Where(h => shaders[h].Stage == Stage.Compute).Order(StringComparer.Ordinal))
                if (compute.Add((cs, rootOf(pass))))
                    maps.Add(new ShaderMap(CarvedReader.Sha1Hex($"{cs}|{rootOf(pass)}"), lib, platform?.Invoke(cs) ?? CarvedReader.Platform, [cs], IsPipeline: true, rootOf(pass)));
        }
        return maps;
    }

    /// <summary>Adds to <paramref name="found"/> each wanted pass the exe didn't name whose table name hashes to it and is a .pars
    /// name in another case (a wrong entry names nothing); the count added.</summary>
    internal static int RulesNames(IReadOnlyDictionary<string, string>? table, Dictionary<uint, string> found, IReadOnlySet<string> lower, IReadOnlySet<uint> wanted)
    {
        var n = 0;
        foreach (var (hex, name) in table ?? new Dictionary<string, string>())
            if (uint.TryParse(hex, NumberStyles.AllowHexSpecifier, null, out var h) && wanted.Contains(h) && !found.ContainsKey(h)
                && PazCrypto.Hash(name) == h && lower.Contains(name.ToLowerInvariant()))
            {
                found[h] = name.ToLowerInvariant();
                n++;
            }
        return n;
    }

    /// <summary>Each wanted pass hash's lowercase name: a word of the exe (letters, digits, '_') that is a .pars name in
    /// another case and hashes to the pass.</summary>
    internal static Dictionary<uint, string> PassNames(string exe, IReadOnlySet<string> lower, IReadOnlySet<uint> wanted)
    {
        var found = new Dictionary<uint, string>();
        if (lower.Count == 0 || wanted.Count == 0) return found;
        int min = lower.Min(n => n.Length), max = lower.Max(n => n.Length);
        void Word(ReadOnlySpan<byte> w)
        {
            if (w.Length < min || w.Length > max) return;
            var s = Encoding.ASCII.GetString(w);
            if (lower.Contains(s.ToLowerInvariant()) && PazCrypto.Hash(w) is var h && wanted.Contains(h)) found[h] = s.ToLowerInvariant();
        }
        static bool IsWord(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_';
        try
        {
            using var f = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            var buf = new byte[(8 << 20) + max + 1];
            int keep = 0;
            var inside = false;   // a word too long to keep runs on from the last block
            while (true)
            {
                var n = f.Read(buf, keep, buf.Length - keep);
                var len = keep + n;
                var start = inside ? -2 : keep > 0 ? 0 : -1;
                for (var i = keep; i < len; i++)
                {
                    if (IsWord(buf[i])) { if (start == -1) start = i; continue; }
                    if (start >= 0) Word(buf.AsSpan(start, i - start));
                    start = -1;
                }
                if (n == 0)
                {
                    if (start >= 0) Word(buf.AsSpan(start, len - start));
                    break;
                }
                inside = start == -2 || start >= 0 && len - start > max;
                keep = start >= 0 && !inside ? len - start : 0;
                if (keep > 0) buf.AsSpan(start, keep).CopyTo(buf);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // no names: no root signatures
        return found;
    }

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        if (!located.TryGetValue(game.Id, out var at))
        {
            Index(game, engine, null, ct);
            at = located[game.Id];
        }
        foreach (var h in sha1s)
            if (at.Roots.TryGetValue(h, out var rs)) sink(h, rs);
        var words = keys.Stored(game);
        foreach (var file in sha1s.Where(at.Shaders.ContainsKey).Select(s => (Sha: s, At: at.Shaders[s])).GroupBy(x => x.At.Paz))
        {
            SafeFileHandle h;
            try { h = OpenPaz(file.Key); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }   // gone since the index
            using (h)
                foreach (var (sha, loc) in file.OrderBy(x => x.At.Entry.Offset))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Pamt.Open(h, loc.Entry, words, loc.KeyByte, out _) is { } raw && Container(raw) is { } c && Convert.ToHexStringLower(SHA1.HashData(c)) == sha) sink(sha, c);
                }
        }
    }
}
