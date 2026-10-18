using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

/// <summary>The upload's X-SCSK-Upload header, sent as base64url of snake_case
/// JSON. <see cref="RsRule"/> to <see cref="PlannerVersion"/> are the plan the game last built from this recording: its
/// root-signature rule, the recorded root signatures the rule rebuilt of those it was checked on, its planner version;
/// then the GPU driver's version and the adapter's name. Each left out when unknown.</summary>
public sealed record UploadMeta(string StoreBuildKey, string ContentHash, string? Engine = null, string? Vendor = null, string? AppVersion = null,
    UploadDll[]? Middleware = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RsRule = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? BuiltOk = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? BuiltN = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PlannerVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DriverVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GpuModel = null);

public sealed record UploadDll(string Name, string Sha1);

/// <summary>The first-launch outcome report, POST /v1/report's body as snake_case
/// JSON. <see cref="GpuModel"/>: the adapter's name only. <see cref="Cold"/>: the pipeline creates the driver compiled;
/// <see cref="Keys"/>: four base64url strings, each the record keys (20 bytes each, back to back) whose slowest create took
/// under 10 ms, 10-50, 50-200, and 200 ms or more.</summary>
public sealed record OutcomeReport(string ContentHash, string Vendor, string? GpuModel, string? DriverVersion, string AppVersion, string? RsRule,
    int PlannerVersion, long Cold = 0, string[]? Keys = null)
{
    // A ceiling, not a sample: 100,000 keys of 20 bytes in base64url are 2.7 MB, within the edge's 4 MB with room to spare
    public const int MaxKeys = 100_000;

    /// <summary>The keys a server from before every key was sent takes: its cap was a 64 KB body.</summary>
    public const int OldMaxKeys = 2000;

    /// <summary>The first launch of <paramref name="exeFileName"/> in the recorder's csv that started after
    /// <paramref name="after"/>, ended (a later launch, its <c>#end</c>, or the watched run <paramref name="played"/>
    /// exiting) and created at least <paramref name="minCreates"/> pipelines; null when there is none yet. Its PSO creates
    /// the driver compiled (<see cref="SessionLog.IsCompile"/>) are counted, and the <see cref="MaxKeys"/> slowest distinct
    /// keys kept, into <paramref name="about"/>. The game's own pipeline library and ray tracing state objects are left
    /// out: a cached state object can take 25 ms.</summary>
    public static OutcomeReport? Build(string csvPath, string? exeFileName, DateTimeOffset after, PlayWindow? played, IReadOnlySet<string>? rayQuery,
        OutcomeReport about, long minCreates = 1)
    {
        var launches = SessionLog.Launches(csvPath).ToList();
        for (var i = 0; i < launches.Count; i++)
        {
            var l = launches[i];
            if (l.Start is not { } start || start <= after.ToUnixTimeMilliseconds() || SessionLog.OtherExe(l.Exe, exeFileName)) continue;
            var exited = played is { } p && p.From.ToUnixTimeMilliseconds() <= start && start <= p.To.ToUnixTimeMilliseconds();
            if (i == launches.Count - 1 && l.End == null && !exited) return null;
            if (l.Creates.Count(c => c.Kind is 'G' or 'C' or 'S') < minCreates) continue;   // a crash at start, a launcher-like run
            var slowest = new Dictionary<string, double>();
            long cold = 0;
            foreach (var (_, kind, ms, key, _, _) in l.Creates)
            {
                if (kind is 'R' or 'A' || !SessionLog.IsCompile(kind, ms, key, rayQuery)) continue;
                cold++;
                if (key is { Length: 40 } && key.All(char.IsAsciiHexDigit))
                    slowest[key.ToLowerInvariant()] = Math.Max(ms, slowest.GetValueOrDefault(key.ToLowerInvariant()));
            }
            var buckets = new List<byte>[] { [], [], [], [] };
            foreach (var (key, ms) in slowest.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(MaxKeys))
                buckets[ms < 10 ? 0 : ms < 50 ? 1 : ms < 200 ? 2 : 3].AddRange(Convert.FromHexString(key));
            return about with { Cold = cold, Keys = [.. buckets.Select(b => Base64Url.EncodeToString([.. b]))] };
        }
        return null;
    }

    /// <summary>This report with only its <paramref name="max"/> slowest keys: the buckets slowest first, each in that
    /// order already.</summary>
    public OutcomeReport Slowest(int max)
    {
        var kept = new string[4];
        for (var b = 3; b >= 0; b--)
        {
            var keys = Base64Url.DecodeFromChars(Keys![b]);
            var n = Math.Min(keys.Length / 20, max);
            kept[b] = Base64Url.EncodeToString(keys.AsSpan(0, n * 20));
            max -= n;
        }
        return this with { Keys = kept };
    }
}

/// <summary>What sharing did with a game's recording (games\&lt;id&gt;\shared.json). <see cref="Stamp"/>: the recording and
/// content hash last looked at to the end (uploaded, or found to have nothing to share); <see cref="Sent"/>: the uploads of
/// it the server has (SHA-256 of an upload's records, "|", its content hash), kept across a pass cut short; the rest is the
/// last upload, if any (<see cref="Psos"/> / <see cref="NewPsos"/>: summed over its uploads). <see cref="Meta"/>: the plan's
/// rule check the server last got with an upload of it.</summary>
public sealed record SharedRecording(string Stamp, DateTimeOffset? At = null, string? UploadId = null, int Psos = 0, int NewPsos = 0, string[]? Sent = null,
    string? Meta = null);

/// <summary>The write side of the community database: a game's own
/// recording (recording.db, never the merge with the community's), stripped to its hash-only form, posted with an anonymous
/// upload device's token. That device is registered once (POST /v1/devices) and kept DPAPI-protected in upload.dat, apart
/// from the Patreon sign-in (auth.dat), which this class never sees. Works signed out. Quiet: failures
/// land in <see cref="Problem"/> and back off; nothing throws but cancellation.</summary>
public sealed class Sharing
{
    public const int MaxBody = 4 << 20;   // = the server's cap
    // One upload: real sessions run 0.8 to 1 KB a record hash-only, so ~20 MB and well under the origin's default 32 MB
    public const int ChunkRecords = 20_000;
    public const long ChunkRaw = 24 << 20;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    readonly string file;
    readonly Func<bool> enabled;
    readonly RouteFailover routes;
    readonly HttpClient http;
    readonly TimeProvider clock;
    DateTimeOffset backoffUntil, reportBackoffUntil;

    /// <param name="enabled">Settings.ShareRecordings, read at each call: off means no work and no request at all</param>
    public Sharing(string dataDir, Func<bool> enabled, RouteFailover? routes = null, TimeProvider? clock = null)
    {
        file = Path.Combine(dataDir, "upload.dat");
        this.enabled = enabled;
        this.routes = routes ?? RouteFailover.Default;
        http = new HttpClient(this.routes, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The last failure, in plain words; null after a success.</summary>
    public string? Problem { get; private set; }

    /// <summary>Why the last share waited without a problem: a recording whose layer records aren't whole yet
    /// (<see cref="Recordings.IncompleteLayerList"/>); null = it didn't. The caller logs it once a pass.</summary>
    public string? Waiting { get; set; }

    /// <summary>The upload device's id (for the admin's trust-device), null before the first registration.</summary>
    public string? DeviceId => Load()?.Id;

    /// <summary>Forgets the upload device; the next upload registers a new one.</summary>
    public void Reset() => File.Delete(file);

    public static SharedRecording? Shared(string gameDir) => Community.Read<SharedRecording>(Path.Combine(gameDir, "shared.json"));

    /// <summary>Uploads <paramref name="gameDir"/>\recording.db for the build <paramref name="contentHash"/> when sharing is on
    /// and it changed since the last look: in uploads of at most <see cref="ChunkRecords"/> records (<see cref="HashOnly.Chunks"/>),
    /// each skipped when the server already has it or the downloaded community entry for that build holds all its records.
    /// <paramref name="meta"/> is built only for an actual upload (it may hash middleware DLLs). <paramref name="planCheck"/>:
    /// the plan's rule check the metadata carries (null: none); when the server hasn't had it with this recording and nothing
    /// else is new, one pipeline goes again (<see cref="Smallest"/>) to carry it. Its D3D11 draws ('D') go apart, under the
    /// build's draws key (<see cref="HashOnly.DrawsKey"/>); a server that refuses that key isn't sent draws again until the
    /// next app version (<see cref="DrawsRefusedFile"/>), and the draws stay local. Returns the new <see cref="SharedRecording"/>
    /// after an upload, else null.</summary>
    public async Task<SharedRecording?> ShareAsync(string gameDir, string contentHash, Func<UploadMeta> meta,
        Func<IEnumerable<string>>? middlewareShaders = null, Func<IReadOnlySet<string>>? layerMade = null, string? planCheck = null,
        CancellationToken ct = default)
    {
        var db = new FileInfo(Path.Combine(gameDir, "recording.db"));
        if (!enabled() || !db.Exists || clock.GetUtcNow() < backoffUntil) return null;
        Problem = null;   // per game; a back-off's stays
        // "|3": re-examines stamps that marked an over-cap or a draws-only recording as done; the shader list: flags once it is there
        var list = new FileInfo(Path.Combine(gameDir, ShippedFile));
        var drawsOff = DrawsRefused();
        var stamp = $"{db.Length}:{db.LastWriteTimeUtc.Ticks}|{contentHash}|3" + (list.Exists ? $"|{list.Length}:{list.LastWriteTimeUtc.Ticks}" : "")
            + (planCheck != null ? $"|{planCheck}" : "") + (drawsOff ? "|nodraws" : "");
        var last = Shared(gameDir) ?? new("");
        if (last.Stamp == stamp) return null;
        var shared = Path.Combine(gameDir, "shared.json");
        List<PsoDb.Rec> records;
        IReadOnlySet<string>? excluded;
        try { excluded = layerMade?.Invoke(); }   // read now, right before the payload; one that can't be read throws: nothing shared
        catch (Recordings.IncompleteLayerList e) { Waiting = e.Message; return null; }
        var all = PsoDb.Read(db.FullName).ToList();
        var draws = all.Where(r => r.Tag == 'D' && Draw11Valid(r)).ToList();   // a malformed one goes alone, the rest still share
        all.RemoveAll(r => r.Tag == 'D');
        records = [];
        if (draws.Count == 0 || all.Any(r => r.Tag != 'B'))
            try { records = HashOnly.Canonical(all, local: true, out _, HashOnly.MaxEntryStateObjectRefs, excluded); }   // shader and DXIL library blobs dropped (state objects kept)
            catch (InvalidDataException e) when (draws.Count == 0)   // e.g. no PSOs yet: not again until the recording changes
            {
                Community.Write(shared, last with { Stamp = stamp });
                Problem = $"A recording has nothing to share ({e.Message}).";
                return null;
            }
            catch (InvalidDataException) { }   // the draws go alone
        var shipped = Shipped(gameDir, contentHash);
        if (shipped != null)
        {
            shipped.UnionWith(middlewareShaders?.Invoke() ?? []);
            if (records.Count > 0) records = HashOnly.Canonical([.. records, .. HashOnly.LocalOnly(records, shipped.Contains)], local: false, out _, HashOnly.MaxEntryStateObjectRefs);
        }
        var drawsKey = HashOnly.DrawsKey(contentHash);
        var drawsHash = HashOnly.PackHash(drawsKey);
        List<PsoDb.Rec> drawRecords = [];
        if (draws.Count > 0 && !drawsOff)
            try { drawRecords = HashOnly.Canonical([.. draws, .. shipped == null ? [] : HashOnly.LocalOnly(draws, shipped.Contains)], local: false, out _); }
            catch (InvalidDataException) { }   // a 'D' this version's recorder wouldn't write: none go
        var have = Community.Downloaded(gameDir)?.ContentHash == contentHash
            ? PsoDb.Read(Path.Combine(gameDir, "community.db")).Where(r => r.Tag != 'B').Select(r => r.Key).ToHashSet() : [];
        var haveDraws = Community.DownloadedDraws(gameDir)?.ContentHash == drawsHash
            ? PsoDb.Read(Path.Combine(gameDir, "draws.db")).Select(r => r.Key).ToHashSet() : [];
        var sent = new List<string>();
        var (uploadId, psos, fresh) = ((string?)null, 0, 0);
        var chunks = HashOnly.Chunks(records, ChunkRecords, ChunkRaw, out var tooLarge);
        // nothing new but the rule check: the server keeps an upload's metadata, and a body it already has returns that upload
        if (planCheck != null && planCheck != last.Meta)
        {
            IReadOnlySet<string>? made = null;
            try { made = layerMade?.Invoke(); }
            catch (Exception) { }   // read again before the upload, which stops on it
            List<PsoDb.Rec>? Kept(List<PsoDb.Rec> c)   // as the loop below sends it
            {
                try { return made == null ? c : HashOnly.Canonical(c, local: true, out _, layerMade: made); }
                catch (InvalidDataException) { return null; }
            }
            if (chunks.All(c => Kept(c) is not { } k || last.Sent?.Contains(Id(k, contentHash)) == true || k.All(r => r.Tag == 'B' || have.Contains(r.Key)))
                && Kept(records) is { } kept && Smallest(kept) is { } one)
                chunks = [one];
        }
        var sentWith = last.Meta;
        if (tooLarge > 0) Problem = $"{tooLarge} ray tracing records are more than an upload may carry with what they build on ({ChunkRecords} records, {ChunkRaw >> 20} MB, {HashOnly.MaxStateObjectRefs} references): shared without them.";
        foreach (var (whole, isDraws) in chunks.Select(c => (c, false)).Concat(HashOnly.Chunks(drawRecords, ChunkRecords, ChunkRaw).Select(c => (c, true))))
        {
            if (isDraws && drawsOff) break;
            // what a layer made, read again right before each payload: one learned during an earlier upload is left out too
            IReadOnlySet<string>? excludedNow;
            try { excludedNow = layerMade?.Invoke(); }
            catch (Recordings.IncompleteLayerList e) { Waiting = e.Message; return Stop(null); }
            catch (Exception e) { return Stop($"What a layer made can't be read ({e.Message}): sharing waits."); }   // any failure: nothing more goes
            List<PsoDb.Rec> chunk;
            try { chunk = excludedNow == null || isDraws ? whole : HashOnly.Canonical(whole, local: true, out _, layerMade: excludedNow); }
            catch (InvalidDataException) { continue; }   // nothing left of it but root signatures
            var id = Id(chunk, isDraws ? drawsHash : contentHash);   // the same records for another build are new there
            if (last.Sent?.Contains(id) == true || chunk.All(r => r.Tag == 'B' || (isDraws ? haveDraws : have).Contains(r.Key)))
            {
                sent.Add(id);
                continue;
            }
            var body = HashOnly.Compress(chunk);
            if (body.Length > MaxBody) return Stop($"A recording is too large to share ({body.Length >> 20} MB compressed, the limit is {MaxBody >> 20} MB).");
            UploadMeta Meta() => isDraws ? meta() with { StoreBuildKey = drawsKey, ContentHash = drawsHash } : meta();
            try
            {
                var r = await PostAsync(body, Meta(), ct);
                if (r?.StatusCode == HttpStatusCode.Unauthorized)   // the device expired (90 days unused) or was revoked: once with a new one
                {
                    r.Dispose();
                    Reset();
                    r = await PostAsync(body, Meta(), ct);
                }
                if (r == null) return Stop(Problem);
                using (r)
                {
                    if (r.StatusCode == HttpStatusCode.Accepted)
                    {
                        var a = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
                        uploadId = a.GetProperty("upload_id").GetString();
                        psos += a.GetProperty("records").GetInt32();
                        fresh += a.GetProperty("new_records").GetInt32();
                        sent.Add(id);
                        if (!isDraws) sentWith = planCheck;
                        continue;
                    }
                    // a server without draws entries refuses the key (400) or the route: quiet, the draws stay local
                    if (isDraws && r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                    {
                        File.WriteAllText(DrawsRefusedPath, AppVersion.Current.ToString());
                        drawsOff = true;
                        stamp += "|nodraws";
                        continue;
                    }
                    var why = $"Sharing a recording was refused (error {(int)r.StatusCode}).";
                    if (r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.Forbidden)
                    {
                        Problem = why;
                        if (r.StatusCode == HttpStatusCode.Forbidden) break;   // this device is blocked: done until the recording changes
                        sent.Add(id);   // refused for good; the other uploads still go
                        continue;
                    }
                    BackOff(r);
                    return Stop(why);
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                backoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5);   // offline, or an answer this version can't read
                return Stop(e is HttpRequestException or OperationCanceledException ? "Can't reach the community database to share a recording." : e.Message);
            }
        }
        var got = uploadId is null ? last with { Stamp = stamp, Sent = [.. sent] } : new SharedRecording(stamp, clock.GetUtcNow(), uploadId, psos, fresh, [.. sent], sentWith);
        Community.Write(shared, got);   // looked at: not again until the recording changes
        return uploadId is null ? null : got;

        // Cut short: not stamped, so the next pass tries again, skipping what the server already has
        SharedRecording? Stop(string? why)
        {
            if (sent.Count > 0) Community.Write(shared, last with { Sent = [.. (last.Sent ?? []).Union(sent)] });
            if (why is not null) Problem = why;
            return null;
        }
    }

    /// <summary>In the data folder: the app version whose draws upload the server refused; none go until another version runs.</summary>
    public const string DrawsRefusedFile = "draws-refused.txt";

    string DrawsRefusedPath => Path.Combine(Path.GetDirectoryName(file)!, DrawsRefusedFile);

    bool DrawsRefused()
    {
        try { return File.Exists(DrawsRefusedPath) && File.ReadAllText(DrawsRefusedPath) == AppVersion.Current.ToString(); }
        catch (IOException) { return false; }
    }

    static bool Draw11Valid(PsoDb.Rec r)
    {
        try { HashOnly.CheckDraw11(r); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>The smallest upload that is valid on its own: the first pipeline of the recording with its root signature
    /// and its 'N' and 'L' records; null without one.</summary>
    internal static List<PsoDb.Rec>? Smallest(List<PsoDb.Rec> canonical) =>
        canonical.FirstOrDefault(r => r.Tag is 'G' or 'C' or 'S') is { } pso
            ? HashOnly.Canonical([.. canonical.Where(r => r.Tag == 'B' || r.Tag is 'N' or 'L' && HashOnly.Target(r) == pso.Key), pso], local: false, out _)
            : null;

    /// <summary>Uploads each pack of a shared vendor (<see cref="Middleware.SharedVendors"/>) in <paramref name="packsDir"/>
    /// (this PC's own, filled from its recordings on the GPU vendor <paramref name="gpu"/>: <see cref="MiddlewarePack.PackHeader.Gpu"/>)
    /// that changed since its last upload, under its pack key for that vendor, when sharing is on. Stamped per pack file in
    /// packs-shared.json, so a pack goes again only once it gains records. Returns the DLL name and PSO count of each upload.</summary>
    /// <paramref name="layered"/>: the records a layer made (<see cref="MiddlewarePacks.LayerMade"/>), read again before each
    /// pack's payload and never in an upload whatever a pack holds; one that can't be read ends the pass.
    public async Task<List<(string Dll, int Psos)>> SharePacksAsync(string packsDir, string gpu, string appVersion, Func<IReadOnlySet<string>>? layered = null,
        CancellationToken ct = default)
    {
        var done = new List<(string, int)>();
        if (!enabled() || clock.GetUtcNow() < backoffUntil) return done;
        var stampFile = Path.Combine(Path.GetDirectoryName(file)!, "packs-shared.json");
        var stamps = Community.Read<Dictionary<string, string>>(stampFile) ?? [];
        var files = Middleware.SharedVendors.Select(v => Path.Combine(packsDir, v)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.pack")).Order(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            foreach (var path in files)
            {
                var fi = new FileInfo(path);
                var stamp = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}|{gpu}";
                var name = Path.GetFileName(path);
                if (stamps.GetValueOrDefault(name) == stamp) continue;
                List<PsoDb.Rec> records;
                MiddlewarePack pack;
                IReadOnlySet<string>? excluded;
                try { excluded = layered?.Invoke(); }
                catch (Recordings.IncompleteLayerList e) { Waiting = e.Message; break; }
                try { records = MiddlewarePacks.Records(pack = MiddlewarePack.Read(path), excluded); }
                catch (Exception e) when (e is InvalidDataException or IOException or JsonException)
                {
                    stamps[name] = stamp;   // nothing to share until it changes
                    continue;
                }
                if (pack.Header.Gpu != gpu)   // another GPU vendor's, or from before packs kept theirs: shared once a recording here adopts it
                {
                    stamps[name] = stamp;
                    continue;
                }
                var key = HashOnly.PackKey(gpu, pack.Header.Vendor, pack.Header.Dll, pack.Header.ContentHash);
                var body = HashOnly.Compress(records);
                if (body.Length > MaxBody)
                {
                    Problem = $"An upscaler pack is too large to share ({body.Length >> 20} MB compressed, the limit is {MaxBody >> 20} MB).";
                    stamps[name] = stamp;
                    continue;
                }
                var meta = new UploadMeta(key, HashOnly.PackHash(key), Vendor: gpu, AppVersion: appVersion);
                var r = await PostAsync(body, meta, ct);
                if (r?.StatusCode == HttpStatusCode.Unauthorized)   // the device expired or was revoked: once with a new one
                {
                    r.Dispose();
                    Reset();
                    r = await PostAsync(body, meta, ct);
                }
                if (r == null) break;
                using (r)
                {
                    if (r.StatusCode == HttpStatusCode.Accepted)
                    {
                        done.Add((pack.Header.Dll, (await r.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("records").GetInt32()));
                        stamps[name] = stamp;
                        continue;
                    }
                    Problem = $"Sharing an upscaler pack was refused (error {(int)r.StatusCode}).";
                    if (r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
                    {
                        stamps[name] = stamp;   // refused for good: not again until it changes
                        continue;
                    }
                    if (r.StatusCode == HttpStatusCode.Forbidden) stamps[name] = stamp;   // blocked: not again until the pack changes, like a recording
                    else BackOff(r);
                    break;
                }
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            backoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5);
            Problem = e is HttpRequestException or OperationCanceledException ? "Can't reach the community database to share an upscaler pack." : e.Message;
        }
        finally { Community.Write(stampFile, stamps); }
        return done;
    }

    /// <summary>The warm whose outcome report was sent (or refused for good), in a game's data folder.</summary>
    public const string ReportedFile = "reported.txt";

    /// <summary>Sends the outcome report of the warm <paramref name="warm"/> once, when sharing is on, with no token or
    /// identifier of any kind (never the upload device); <paramref name="report"/> is built only for a warm not reported yet
    /// (null: nothing to report yet). A report of more than <see cref="OutcomeReport.OldMaxKeys"/> keys refused with 400 or
    /// 413 goes once more with its slowest <see cref="OutcomeReport.OldMaxKeys"/>, which an older server takes. Stamped in
    /// <see cref="ReportedFile"/> once the server takes it (2xx) or refuses it for good (400, 413). A server without
    /// the route (404, 501) is asked again a day later. Quiet: a refused or failed report sets no <see cref="Problem"/>.
    /// Returns whether it was sent.</summary>
    public async Task<bool> ReportAsync(string gameDir, string warm, Func<OutcomeReport?> report, CancellationToken ct = default)
    {
        var stamp = Path.Combine(gameDir, ReportedFile);
        if (!enabled() || clock.GetUtcNow() < reportBackoffUntil || File.Exists(stamp) && File.ReadAllText(stamp) == warm || report() is not { } r) return false;
        async Task<HttpResponseMessage> Post(OutcomeReport sent)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(routes.Primary, "v1/report")) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(sent, Json)) };
            request.Content.Headers.ContentType = new("application/json");
            return await http.SendAsync(request, ct);
        }
        try
        {
            var res = await Post(r);
            if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge
                && r.Keys?.Sum(k => Base64Url.DecodeFromChars(k).Length) > OutcomeReport.OldMaxKeys * 20)
            {
                res.Dispose();
                res = await Post(r.Slowest(OutcomeReport.OldMaxKeys));
            }
            using var _ = res;
            if (res.IsSuccessStatusCode || res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
            {
                Directory.CreateDirectory(gameDir);
                File.WriteAllText(stamp, warm);
                return res.IsSuccessStatusCode;
            }
            reportBackoffUntil = clock.GetUtcNow() + (res.StatusCode is HttpStatusCode.NotFound ? TimeSpan.FromDays(1) : Delay(res));
            return false;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            reportBackoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5);
            return false;
        }
    }

    /// <summary>POST /v1/compat of <paramref name="report"/> with the upload device's token, registering the device first if
    /// there is none. Only on the user's click: <see cref="Settings.ShareRecordings"/> doesn't apply, and nothing retries
    /// later. Null when the server took it (200), else what to tell the user. A 401 (the device expired or was revoked) goes
    /// once more with a new device. A server without reports (404, 501) isn't asked again until the next start. Never throws
    /// but cancellation.</summary>
    public async Task<string?> SendCompatAsync(CompatReport report, CancellationToken ct = default)
    {
        if (compatClosed) return CompatClosed;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                if (await DeviceTokenAsync(ct) is not { } token) return "Couldn't register this PC for reports. Try again later.";
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(routes.Primary, "v1/compat")) { Content = new ByteArrayContent(report.Body()) };
                request.Content.Headers.ContentType = new("application/json");
                request.Headers.Authorization = new("Bearer", token);
                using var r = await http.SendAsync(request, ct);
                if (r.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    Reset();
                    continue;
                }
                compatClosed = r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented;
                return r.StatusCode switch
                {
                    HttpStatusCode.OK => null,
                    HttpStatusCode.BadRequest => "The server didn't take this report.",
                    _ when compatClosed => CompatClosed,
                    _ => "Not sent. Try again later.",
                };
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)   // offline, or an answer this version can't read
        {
            return "Not sent. Try again later.";
        }
    }

    const string CompatClosed = "Reports aren't open yet.";
    bool compatClosed;

    /// <summary>A game folder's list of the shaders its build ships (the index's): the content hash (20 bytes), then each
    /// shader's SHA-1. What an upload flags against (<see cref="HashOnly.LocalOnly"/>).</summary>
    public const string ShippedFile = "index.shaders";

    public static void SaveShipped(string gameDir, ShaderIndex index)
    {
        if (index.ContentHash.Length != 40) return;   // not a build Share uploads for
        Directory.CreateDirectory(gameDir);
        var tmp = Path.Combine(gameDir, ShippedFile + ".tmp");
        using (var f = new BufferedStream(File.Create(tmp), 1 << 20))
            foreach (var h in index.Shaders.Keys.Prepend(index.ContentHash)) f.Write(Convert.FromHexString(h));
        File.Move(tmp, Path.Combine(gameDir, ShippedFile), true);
    }

    /// <summary>The shaders of <see cref="ShippedFile"/>, null when it's missing or for another build.</summary>
    internal static HashSet<string>? Shipped(string gameDir, string contentHash)
    {
        var path = Path.Combine(gameDir, ShippedFile);
        var b = File.Exists(path) ? File.ReadAllBytes(path) : [];
        if (b.Length < 20 || b.Length % 20 != 0 || Convert.ToHexStringLower(b.AsSpan(0, 20)) != contentHash) return null;
        return [.. b.Chunk(20).Skip(1).Select(Convert.ToHexStringLower)];
    }

    static string Id(List<PsoDb.Rec> chunk, string contentHash)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var r in chunk)
        {
            h.AppendData([(byte)r.Tag, .. BitConverter.GetBytes(r.Payload.Length)]);
            h.AppendData(r.Payload);
        }
        return $"{Convert.ToHexStringLower(h.GetHashAndReset())}|{contentHash}";
    }

    /// <summary>POST /v1/upload with the upload device's token (registered first if there is none); null when sharing is off or no device could be had.</summary>
    async Task<HttpResponseMessage?> PostAsync(byte[] body, UploadMeta meta, CancellationToken ct)
    {
        if (!enabled() || await DeviceTokenAsync(ct) is not { } token || !enabled()) return null;   // again: turned off while registering
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(routes.Primary, "v1/upload")) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("X-SCSK-Upload", Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(meta, Json)));
        return await http.SendAsync(request, ct);
    }

    async Task<string?> DeviceTokenAsync(CancellationToken ct)
    {
        if (Load()?.Token is { } token) return token;
        using var r = await http.PostAsync(new Uri(routes.Primary, "v1/devices"), null, ct);
        if (!r.IsSuccessStatusCode)
        {
            BackOff(r);   // 3 registrations a day per IP
            Problem = $"Couldn't register for sharing (error {(int)r.StatusCode}).";
            return null;
        }
        var j = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
        var device = new Device(j.GetProperty("device_token").GetString()!, j.GetProperty("device_id").GetString()!);
        Dpapi.Save(file, device);
        return device.Token;
    }

    void BackOff(HttpResponseMessage r) => backoffUntil = clock.GetUtcNow() + Delay(r);

    // 429/503: as long as Retry-After says (1 h without one); 501 (not enabled on this server): a day; others: 5 minutes
    TimeSpan Delay(HttpResponseMessage r) => r.StatusCode switch
    {
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => r.Headers.RetryAfter switch
        {
            { Delta: { } d } => d,
            { Date: { } at } => at - clock.GetUtcNow(),
            _ => TimeSpan.FromHours(1),
        },
        HttpStatusCode.NotImplemented => TimeSpan.FromDays(1),
        _ => TimeSpan.FromMinutes(5),
    };

    // upload.dat: {"Token":"sd1_...","Id":"..."} under DPAPI, like auth.dat but its own file: never the Patreon device
    sealed record Device(string Token, string Id);

    Device? Load() => Dpapi.Load<Device>(file);   // null (another user's or damaged): register again
}
