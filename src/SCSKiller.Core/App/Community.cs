using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

/// <summary>A manifest entry: a game build's merged hash-only recording. <see cref="Object"/> is the
/// lowercase hex SHA-256 of the bytes served at /v1/o/&lt;Object&gt;, <see cref="Size"/> their length. <see cref="Full"/>: an
/// 'X' record's, read with the full entry's caps.</summary>
public sealed record CommunityEntry(string ContentHash, string Object, long Size, int Psos, int Uploaders, bool Full = false);

/// <summary>Who may download an official recording: byte 1 of its 'O' record. Another value is a
/// rule this version doesn't know: never downloaded.</summary>
public enum OfficialVisibility : byte { Prio = 0, Patreon = 1, Everyone = 2 }

/// <summary>A manifest 'O' record: the maintainer's hash-only recording for a game build, kept apart from the community entry.</summary>
public sealed record OfficialEntry(string ContentHash, string Object, long Size, int Psos, OfficialVisibility Visibility);

/// <summary>What was downloaded for a game (games\&lt;id&gt;\community.json next to community.db; official.json next to official.db).</summary>
public sealed record CommunityDownload(string Object, string ContentHash, int Psos, DateTimeOffset DownloadedAt,
    OfficialVisibility? Visibility = null,   // an official recording's, as downloaded; null for a community one
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Full = false,
    // the index content hash of an install that has none of its shaders: not used there, kept for a later build
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? MatchesNothingIn = null);

/// <summary>The manifest: fixed 80-byte little-endian records, replayed in file order. Record 0 is the
/// 'M' header (format 1, the epoch); 'E' entry: content hash -> object (the last one wins); 'P' the same for a middleware
/// pack, by its pack hash; 'X' the same for an entry's full cut (500,000 records, 'L' not counted), read only where
/// <see cref="Find"/> is asked for it; 'D' the same for a game build's D3D11 draws, by the pack hash of its draws key
/// (<see cref="HashOnly.DrawsKey"/>); 'A' alias: SHA-1 of the store build key -> content hash; 'T' tombstone: withdraws an
/// entry (both cuts), pack or draws until a later 'E', 'X', 'P' or 'D', and the official recording of that hash; 'O' official recording: content hash ->
/// object, byte 1 its visibility (the last one wins), an all-zero object withdrawing the official recording only.
/// Unknown kinds are skipped.</summary>
public sealed class CommunityManifest
{
    public const int RecordSize = 80;
    readonly Dictionary<string, CommunityEntry> entries = [], fulls = [], packs = [], draws = [];
    readonly Dictionary<string, string> aliases = [];
    readonly Dictionary<string, OfficialEntry> officials = [];
    public uint Epoch { get; }
    /// <summary>The file's length in bytes: it grows with every record.</summary>
    public long Length { get; private init; }

    CommunityManifest(uint epoch) => Epoch = epoch;

    /// <summary>InvalidDataException when it isn't a format-1 manifest of whole records.</summary>
    public static CommunityManifest Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < RecordSize || file.Length % RecordSize != 0 || file[0] != 'M' || file[1] != 1)
            throw new InvalidDataException("not a format-1 community manifest");
        var m = new CommunityManifest(BinaryPrimitives.ReadUInt32LittleEndian(file[4..])) { Length = file.Length };
        for (var at = RecordSize; at < file.Length; at += RecordSize)
        {
            var r = file.Slice(at, RecordSize);
            var hash = Convert.ToHexStringLower(r.Slice(4, 20));
            switch ((char)r[0])
            {
                case 'E' or 'X' or 'P' or 'D':
                    (r[0] switch { (byte)'E' => m.entries, (byte)'X' => m.fulls, (byte)'P' => m.packs, _ => m.draws })[hash] = new(hash, Convert.ToHexStringLower(r.Slice(24, 32)), BinaryPrimitives.ReadUInt32LittleEndian(r[56..]),
                        (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(r[60..])), BinaryPrimitives.ReadUInt16LittleEndian(r[2..]), r[0] == 'X');
                    break;
                case 'O' when !r.Slice(24, 32).ContainsAnyExcept((byte)0):
                    m.officials.Remove(hash);
                    break;
                case 'O':
                    m.officials[hash] = new(hash, Convert.ToHexStringLower(r.Slice(24, 32)), BinaryPrimitives.ReadUInt32LittleEndian(r[56..]),
                        (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(r[60..])), (OfficialVisibility)r[1]);
                    break;
                case 'A': m.aliases[hash] = Convert.ToHexStringLower(r.Slice(24, 20)); break;
                case 'T':
                    m.entries.Remove(hash);
                    m.fulls.Remove(hash);
                    m.packs.Remove(hash);
                    m.draws.Remove(hash);
                    m.officials.Remove(hash);
                    break;
            }
        }
        return m;
    }

    /// <summary>The alias key of a store build: lowercase hex SHA-1 of the UTF-8 "&lt;Game.Id&gt;@&lt;Game.Version&gt;"; null
    /// when the store gives no build id (such a game is found by its content hash once indexed).</summary>
    public static string? AliasKey(Game g) => g.Version is { Length: > 0 } v ? Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{g.Id}@{v}"))) : null;

    public bool HasPacks => packs.Count > 0;

    /// <summary>The shared pack of a pack key (<see cref="HashOnly.PackKey"/>); its <see cref="CommunityEntry.ContentHash"/> is the pack hash.</summary>
    public CommunityEntry? FindPack(string packKey) => packs.GetValueOrDefault(HashOnly.PackHash(packKey));

    /// <summary>The D3D11 draws entry of the build <paramref name="contentHash"/>: by content hash only, a draws entry has no alias.</summary>
    public CommunityEntry? FindDraws(string contentHash) =>
        ShaderIndex.IsEmpty(contentHash) ? null : draws.GetValueOrDefault(HashOnly.PackHash(HashOnly.DrawsKey(contentHash)));

    /// <summary>By content hash when given (a fresh index), else by the game's store build. <paramref name="full"/>: the
    /// entry's 'X' when it has one.</summary>
    public CommunityEntry? Find(Game g, string? contentHash, bool full = false) =>
        (full ? Lookup(fulls, g, contentHash) : null) ?? Lookup(entries, g, contentHash);

    /// <summary>The official recording of a build, matched as <see cref="Find"/> matches.</summary>
    public OfficialEntry? FindOfficial(Game g, string? contentHash) => Lookup(officials, g, contentHash);

    /// <summary>Never by <see cref="ShaderIndex.EmptyContentHash"/>, whether given or aliased: it would pool unrelated games.</summary>
    T? Lookup<T>(Dictionary<string, T> by, Game g, string? contentHash) where T : class =>
        (contentHash ?? (AliasKey(g) is { } k && aliases.TryGetValue(k, out var h) ? h : null)) is { } hash && !ShaderIndex.IsEmpty(hash)
            ? by.GetValueOrDefault(hash) : null;
}

/// <summary>The read side of the community shader hash database: the manifest,
/// kept under %LOCALAPPDATA%\SCSKiller\community and brought up to date at most every <see cref="ManifestMaxAge"/> (a Range
/// request for the records after the copy's end), and recordings downloaded with an access token that carries "db",
/// checked by SHA-256 and by content, then kept for good. Every failure (offline, 401/403, 429,
/// the server down) lands in <see cref="Problem"/>; nothing throws but cancellation, so a scan or a compile always goes on
/// locally.</summary>
public sealed class Community
{
    public static readonly TimeSpan ManifestMaxAge = TimeSpan.FromHours(4);
    public const int MaxRaw = 320 << 20;   // decompressed: 1,342 bytes a record at HashOnly.MaxEntryRecords
    public const int MaxFullRaw = 768 << 20;   // an 'X' entry's MaxRaw

    readonly string dir;
    readonly RouteFailover routes;
    readonly HttpClient http;
    readonly TimeProvider clock;
    readonly Func<bool, CancellationToken, Task<string?>> dbToken;
    readonly SemaphoreSlim manifestGate = new(1, 1);
    CommunityManifest? manifest;
    DateTimeOffset backoffUntil, packBackoffUntil;
    volatile bool expired;
    long manifestFailedAt;   // clock.GetTimestamp(): a clock change doesn't move the wait
    long freeRefusedAt = -1;   // the manifest's length when a download without a token was refused
    TimeSpan manifestWait;

    /// <param name="dbToken">(fresh, ct): an access token that carries "db", else null (signed out, or not a supporter);
    /// fresh: a new one, after a 401 (<see cref="Account.GetDbTokenAsync"/>)</param>
    public Community(string dataDir, Func<bool, CancellationToken, Task<string?>> dbToken, RouteFailover? routes = null, TimeProvider? clock = null)
    {
        dir = Path.Combine(dataDir, "community");
        this.routes = routes ?? RouteFailover.Default;
        http = new HttpClient(this.routes, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        this.clock = clock ?? TimeProvider.System;
        this.dbToken = dbToken;
    }

    /// <summary>(flag, fresh, ct): an access token that carries the flag, else null. Without it no "prio" official
    /// recording downloads.</summary>
    public Func<string, bool, CancellationToken, Task<string?>>? TokenWith { get; init; }

    /// <summary>The last failure, in plain words; null after a success.</summary>
    public string? Problem { get; private set; }

    /// <summary>The next <see cref="ManifestAsync"/> checks the server whatever the copy's age (a user's refresh).</summary>
    public void Expire() => expired = true;

    /// <summary>The manifest: the local copy while it was checked less than <see cref="ManifestMaxAge"/> ago (its file's write
    /// time) and not <see cref="Expire"/>d, else brought up to date first (3 s per route). A failed check keeps the copy (null when there's none);
    /// after a 429 or 503 none is made until its Retry-After (1 h without one, a day at most), offline for 5 minutes.</summary>
    public async Task<CommunityManifest?> ManifestAsync(CancellationToken ct = default)
    {
        await manifestGate.WaitAsync(ct);
        try
        {
            var file = Path.Combine(dir, "manifest.bin");
            if (manifest == null && File.Exists(file))
                try { manifest = CommunityManifest.Parse(File.ReadAllBytes(file)); }
                catch (InvalidDataException) { }   // damaged: fetched whole
            if (manifest != null && !expired && clock.GetUtcNow() - File.GetLastWriteTimeUtc(file) < ManifestMaxAge) return manifest;
            if (clock.GetElapsedTime(manifestFailedAt) < manifestWait) return manifest;
            expired = false;
            var have = manifest != null && File.Exists(file) ? File.ReadAllBytes(file) : null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(routes.Primary, $"v1/manifest/{manifest?.Epoch ?? 0}"));
                request.Options.Set(RouteFailover.AttemptTimeout, TimeSpan.FromSeconds(3));
                if (have != null) request.Headers.Range = new(have.Length, null);
                using var r = await http.SendAsync(request, ct);
                var next = r.StatusCode switch
                {
                    HttpStatusCode.RequestedRangeNotSatisfiable when have != null => have,   // nothing new
                    HttpStatusCode.PartialContent when have != null && r.Content.Headers.ContentRange?.From == have.Length =>
                        [.. have, .. await r.Content.ReadAsByteArrayAsync(ct)],
                    HttpStatusCode.OK => await r.Content.ReadAsByteArrayAsync(ct),   // the whole file: a first fetch or a compaction
                    _ => null,
                };
                if (next == null)
                {
                    Problem = Refused(r);
                    if (r.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) ManifestBackoff(RetryAt(r) - clock.GetUtcNow());
                }
                else
                {
                    var parsed = CommunityManifest.Parse(next);   // before saving: a body this version can't read never replaces a good copy
                    if (next != have) AppStore.WriteAtomic(file, next);
                    File.SetLastWriteTimeUtc(file, clock.GetUtcNow().UtcDateTime);   // checked now
                    (manifest, Problem) = (parsed, null);
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Problem = Plain(e);
                if (e is HttpRequestException or OperationCanceledException) ManifestBackoff(TimeSpan.FromMinutes(5));   // offline: not at every scan
            }
            return manifest;
        }
        finally { manifestGate.Release(); }
    }

    // the edge's longest window is a day: a larger or negative value is not believed
    void ManifestBackoff(TimeSpan wait) =>
        (manifestFailedAt, manifestWait) = (clock.GetTimestamp(), wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait);

    /// <summary>Downloads <paramref name="e"/> into <paramref name="gameDir"/>\community.db (a compact recording, + community.json) when an access
    /// token with "db" is at hand. Null when not entitled, while backing off after a 429/503, or on any failure
    /// (<see cref="Problem"/>); what was downloaded before stays.</summary>
    public Task<CommunityDownload?> DownloadAsync(CommunityEntry e, string gameDir, CancellationToken ct = default) =>
        Download(e.Object, e.Size, e.ContentHash, dbToken, gameDir, "community", null, ct, e.Full);

    /// <summary>Downloads the D3D11 draws entry <paramref name="e"/> into <paramref name="gameDir"/>\draws.db (+ draws.json), as
    /// <see cref="DownloadAsync"/>.</summary>
    public Task<CommunityDownload?> DownloadDrawsAsync(CommunityEntry e, string gameDir, CancellationToken ct = default) =>
        Download(e.Object, e.Size, e.ContentHash, dbToken, gameDir, "draws", null, ct);

    /// <summary>Downloads the official recording <paramref name="e"/> into <paramref name="gameDir"/>\official.db (+ official.json)
    /// when this install may: everyone with no token at all, patreon with "db", prio with "prio"; else as <see cref="DownloadAsync"/>.</summary>
    public Task<CommunityDownload?> DownloadOfficialAsync(OfficialEntry e, string gameDir, CancellationToken ct = default) => e.Visibility switch
    {
        // a server that gates every object on "db" refuses it: not asked again until the manifest changes
        OfficialVisibility.Everyone when freeRefusedAt == (manifest?.Length ?? 0) => Task.FromResult<CommunityDownload?>(null),
        OfficialVisibility.Everyone => Download(e.Object, e.Size, e.ContentHash, null, gameDir, "official", e.Visibility, ct),
        OfficialVisibility.Patreon => Download(e.Object, e.Size, e.ContentHash, dbToken, gameDir, "official", e.Visibility, ct),
        OfficialVisibility.Prio when TokenWith is { } with =>
            Download(e.Object, e.Size, e.ContentHash, (fresh, c) => with("prio", fresh, c), gameDir, "official", e.Visibility, ct),
        _ => Task.FromResult<CommunityDownload?>(null),
    };

    async Task<CommunityDownload?> Download(string obj, long size, string contentHash, Func<bool, CancellationToken, Task<string?>>? token,
        string gameDir, string name, OfficialVisibility? visibility, CancellationToken ct, bool full = false)
    {
        if (clock.GetUtcNow() < backoffUntil) return null;
        try
        {
            if (size > (full ? MaxFullRaw : MaxRaw)) return null;
            string? bearer = null;
            if (token != null && (bearer = await token(false, ct)) == null) return null;
            var r = await GetObject(obj, bearer, ct);
            if (r.StatusCode == HttpStatusCode.Unauthorized && token != null && await token(true, ct) is { } fresh)   // expired or revoked: once with a new one
            {
                r.Dispose();
                r = await GetObject(obj, fresh, ct);
            }
            using (r)
            {
                if (token == null && r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    freeRefusedAt = manifest?.Length ?? 0;   // quiet: nothing the user can do about it
                    return null;
                }
                if (!r.IsSuccessStatusCode)
                {
                    if (r.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)   // quota; the fallback route's budget
                        backoffUntil = RetryAt(r);
                    Problem = Refused(r);
                    return null;
                }
                var packed = await Body(r, (int)size, ct);
                if (packed == null || Convert.ToHexStringLower(SHA256.HashData(packed)) != obj)
                {
                    Problem = "A community recording didn't match its checksum and was ignored. It's downloaded again at the next scan.";
                    return null;
                }
                byte[]? file;
                int? checkedPsos;
                if (name == "draws")   // draws.db stays a proxy db: DrawsInUse checks its bytes again
                {
                    file = await Bounded(new BrotliStream(new MemoryStream(packed), CompressionMode.Decompress), MaxRaw, ct);
                    checkedPsos = file == null ? null : Check(file, draws: true);
                }
                else checkedPsos = CheckPacked(packed, out file, full);
                if (checkedPsos is not { } psos)
                {
                    Problem = "A community recording this version can't read was ignored. An update of SCSKiller may fix it.";
                    return null;
                }
                var got = new CommunityDownload(obj, contentHash, psos, clock.GetUtcNow(), visibility, full);
                using (Recordings.Lock(Path.Combine(gameDir, "recording.db"), ct: ct))   // not while a compile (any process's) reads the recordings it prepares
                {
                    // a db left without its json is never used: an 'X' must not stay labelled as the 'E' it replaced
                    if (name == "community") File.Delete(Path.Combine(gameDir, "community.json"));
                    AppStore.WriteAtomic(Path.Combine(gameDir, name + ".db"), file!);
                    Write(Path.Combine(gameDir, name + ".json"), got);
                }
                Problem = null;
                return got;
            }
        }
        catch (Exception x) when (!ct.IsCancellationRequested)
        {
            if (x is HttpRequestException or OperationCanceledException) backoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5);   // offline: not once per game
            Problem = Plain(x);
            return null;
        }
    }

    /// <summary>Downloads the shared pack <paramref name="e"/> (GET /v1/p/, no token: free for every install, signed in or
    /// not) for this install's <paramref name="dll"/> and writes it into <paramref name="shared"/>: only the PSOs that copy of
    /// the DLL can give (<see cref="MiddlewarePacks.FromShared"/>). The PSO count kept, or null on any failure
    /// (<see cref="Problem"/>) or while backing off; a pack downloaded before stays.</summary>
    public async Task<int?> DownloadPackAsync(CommunityEntry e, MiddlewareDll dll, MiddlewareImage image, MiddlewarePacks shared, bool amd, CancellationToken ct = default)
    {
        if (clock.GetUtcNow() < packBackoffUntil || e.Size > MaxRaw) return null;
        try
        {
            using var r = await http.GetAsync(new Uri(routes.Primary, "v1/p/" + e.Object), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!r.IsSuccessStatusCode)
            {
                if (r.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    packBackoffUntil = RetryAt(r);
                Problem = Refused(r);
                return null;
            }
            var packed = await Body(r, (int)e.Size, ct);
            if (packed == null || Convert.ToHexStringLower(SHA256.HashData(packed)) != e.Object)
            {
                Problem = "An upscaler pack didn't match its checksum and was ignored. It's downloaded again at the next scan.";
                return null;
            }
            MiddlewarePack pack;
            try { pack = MiddlewarePacks.FromShared(HashOnly.CheckPack(HashOnly.Canonical(HashOnly.Decompress(packed, MaxRaw), local: false, out _, HashOnly.MaxEntryStateObjectRefs, maxRecords: HashOnly.MaxEntryRecords)), dll, image, amd, e.Object, out _); }
            catch (InvalidDataException)
            {
                Problem = "An upscaler pack this version can't read was ignored. An update of SCSKiller may fix it.";
                return null;
            }
            pack.Write(shared.PathOf(dll.Vendor, dll.Name, image.ContentHash));
            Problem = null;
            return pack.Entries.Count;
        }
        catch (Exception x) when (!ct.IsCancellationRequested)
        {
            if (x is HttpRequestException or OperationCanceledException) packBackoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5);
            Problem = Plain(x);
            return null;
        }
    }

    Task<HttpResponseMessage> GetObject(string obj, string? token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(routes.Primary, "v1/o/" + obj));
        if (token != null) request.Headers.Authorization = new("Bearer", token);
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>What <see cref="DownloadAsync"/> left in a game's folder; null when nothing (or it's unreadable).</summary>
    public static CommunityDownload? Downloaded(string gameDir) => Downloaded(gameDir, "community");

    /// <summary>The visibility kept with the official recording downloaded in <paramref name="gameDir"/> follows the manifest's
    /// while <paramref name="e"/> names the same object, with no download. True when it changed.</summary>
    public static bool Restate(string gameDir, OfficialEntry e, CancellationToken ct = default)
    {
        CommunityDownload? Differs() => DownloadedOfficial(gameDir) is { } d && d.Object == e.Object && d.Visibility != e.Visibility ? d : null;
        if (Differs() == null) return false;
        using (Recordings.Lock(Path.Combine(gameDir, "recording.db"), ct: ct))   // a download may replace it meanwhile
        {
            if (Differs() is not { } d) return false;
            Write(Path.Combine(gameDir, "official.json"), d with { Visibility = e.Visibility });
            return true;
        }
    }

    /// <summary>What <see cref="DownloadDrawsAsync"/> left in a game's folder; null when nothing (or it's unreadable).</summary>
    public static CommunityDownload? DownloadedDraws(string gameDir) => Downloaded(gameDir, "draws");

    /// <summary>What <see cref="DownloadOfficialAsync"/> left in a game's folder; null when nothing (or it's unreadable).</summary>
    public static CommunityDownload? DownloadedOfficial(string gameDir) => Downloaded(gameDir, "official");

    static CommunityDownload? Downloaded(string gameDir, string name) =>
        File.Exists(Path.Combine(gameDir, name + ".db")) ? Read<CommunityDownload>(Path.Combine(gameDir, name + ".json")) : null;

    /// <summary>The PSO count of a hash-only recording that is safe to plan from, else null: only <see cref="HashOnly.Tags"/>
    /// (an 'N' or 'L' about a record it has), read to the last byte; every PSO and state object parses, and a state object has its root
    /// signatures and the records it builds on; every 'B' is a root signature (a DXBC container of RTS0 parts only) under
    /// its own SHA-1. So an entry can't bring shader code: shaders and DXIL libraries only ever come from the local install (Rehydrate).
    /// The same check the server applies to uploads (<see cref="HashOnly.Canonical"/>): a game build's holds no 'D', a draws
    /// entry's (<paramref name="draws"/>) nothing else.</summary>
    public static int? Check(byte[] raw, bool draws = false)
    {
        try
        {
            var canonical = HashOnly.Canonical(HashOnly.Records(raw, HashOnly.RemoteLimit), local: false, out _, HashOnly.MaxEntryStateObjectRefs, maxRecords: HashOnly.MaxEntryRecords);
            return HashOnly.Count(draws ? HashOnly.CheckDraws(canonical) : HashOnly.CheckNoDraws(canonical)).Psos;
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary><see cref="Check"/> of a downloaded game build's entry or official recording (Brotli), as it decodes: keys are
    /// held, not records. <paramref name="compact"/>: what to keep, the object as it came in a compact recording.
    /// <paramref name="full"/>: an 'X' entry's caps, <see cref="MaxFullRaw"/> and <see cref="HashOnly.MaxFullRecords"/> with 'L' apart.</summary>
    internal static int? CheckPacked(byte[] packed, out byte[]? compact, bool full = false)
    {
        compact = null;
        try
        {
            using var b = new BrotliStream(new MemoryStream(packed), CompressionMode.Decompress);
            var (psos, raw) = full
                ? HashOnly.Verify(HashOnly.Records(new BufferedStream(b, 1 << 16), MaxFullRaw), HashOnly.MaxEntryStateObjectRefs, HashOnly.MaxFullRecords, flagsApart: true)
                : HashOnly.Verify(HashOnly.Records(new BufferedStream(b, 1 << 16), MaxRaw), HashOnly.MaxEntryStateObjectRefs, HashOnly.MaxEntryRecords);
            compact = PsoDb.Compact(raw, packed);
            return psos;
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary><paramref name="output"/> = <paramref name="first"/>'s records (none when it doesn't exist), then the
    /// records of each of <paramref name="more"/> that the ones before lack (by Rec.Key) and <paramref name="leaveOut"/>
    /// doesn't name, every 'B' first, as a proxy db.</summary>
    public static void Union(string first, IReadOnlyList<string> more, string output, IReadOnlySet<string>? leaveOut = null)
    {
        // streamed: a local recording with its shader bytes can be large
        var have = File.Exists(first) ? PsoDb.Read(first).Select(r => r.Key).ToHashSet() : [];
        if (leaveOut != null) have.UnionWith(leaveOut);
        IEnumerable<PsoDb.Rec> Pass(bool blobs) => (File.Exists(first) ? PsoDb.Read(first) : []).Where(r => r.Tag == 'B' == blobs)
            .Concat(more.SelectMany(m => PsoDb.Read(m).Where(r => r.Tag == 'B' == blobs && have.Add(r.Key))));
        var tmp = output + ".tmp";
        using (var f = new BufferedStream(File.Create(tmp), 1 << 20))
            foreach (var r in Pass(true).Concat(Pass(false))) PsoDb.Write(f, r.Tag, r.Payload);
        File.Move(tmp, output, true);
    }

    /// <summary>How long a download's body may send nothing before it's given up (OperationCanceledException): the
    /// client's timeout ends at the response headers, and a background pass has no cancellation of its own.</summary>
    public TimeSpan BodyIdle { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A response body of at most <paramref name="max"/> bytes, null past it; cancelled after <see cref="BodyIdle"/> without a byte.</summary>
    async Task<byte[]?> Body(HttpResponseMessage r, int max, CancellationToken ct)
    {
        using var idle = new CancellationTokenSource(BodyIdle, clock);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        return await Bounded(await r.Content.ReadAsStreamAsync(either.Token), max, either.Token, () => idle.CancelAfter(BodyIdle));
    }

    static async Task<byte[]?> Bounded(Stream s, int max, CancellationToken ct, Action? progress = null)
    {
        await using (s)
        {
            var buf = new MemoryStream();
            var chunk = new byte[81920];
            for (int n; (n = await s.ReadAsync(chunk, ct)) > 0;)
            {
                if (buf.Length + n > max) return null;
                if (buf.Length + n > buf.Capacity) buf.Capacity = (int)Math.Min(max, Math.Max(2L * buf.Capacity, buf.Length + n));   // MemoryStream's own doubling would pass max
                buf.Write(chunk, 0, n);
                progress?.Invoke();
            }
            return buf.ToArray();
        }
    }

    // Retry-After as seconds or as a date; an hour without one
    DateTimeOffset RetryAt(HttpResponseMessage r) => r.Headers.RetryAfter switch
    {
        { Delta: { } d } => clock.GetUtcNow() + d,
        { Date: { } at } => at,
        _ => clock.GetUtcNow() + TimeSpan.FromHours(1),
    };

    static string Refused(HttpResponseMessage r) => r.StatusCode switch
    {
        HttpStatusCode.Unauthorized => "The community database didn't accept this PC's sign-in. Refresh status or sign in again in Settings.",
        HttpStatusCode.Forbidden => "Your Patreon membership doesn't include the community database right now.",
        HttpStatusCode.TooManyRequests => "The community database's daily limit is reached. Downloads resume later by themselves.",
        HttpStatusCode.ServiceUnavailable => "The community database is busy right now. Downloads resume later by themselves.",
        >= HttpStatusCode.InternalServerError => $"The community database had a problem (error {(int)r.StatusCode}). It retries at the next scan.",
        _ => $"The community database refused the request (error {(int)r.StatusCode}).",
    };

    static string Plain(Exception e) => e switch
    {
        HttpRequestException => "Can't reach the community database right now. Compiling works as usual from your own files.",
        OperationCanceledException => "The community database didn't answer in time. It retries at the next scan.",
        AccountException => e.Message,
        InvalidDataException => "The community database sent a list this version can't read. An update of SCSKiller may fix it.",
        _ => "The community database gave an answer this version doesn't understand. An update of SCSKiller may fix it.",
    };

    internal static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) : null; }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    internal static void Write<T>(string path, T value) => AppStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value));
}
