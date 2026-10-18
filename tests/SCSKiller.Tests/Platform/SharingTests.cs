using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using static SCSKiller.Tests.Platform.CommunityTests;

namespace SCSKiller.Tests.Platform;

// Anonymous uploads of this PC's recordings against a fake handler: no network,
// synthetic recordings only.
public class SharingTests : IDisposable
{
    static readonly Uri Com = new("https://api.test.com/"), Io = new("https://api.test.io/");
    const string Content = "0123456789abcdef0123456789abcdef01234567", Anon = "sd1_test-anon", Patreon = "sd1_patreon-member-device";
    internal static readonly byte[] Shader = "DXBC not a root signature: shader code that must never leave the PC"u8.ToArray();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-sharing-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Clock _clock = new();
    readonly List<(string Line, byte[] Body, string? Meta)> _sent = [];
    Func<HttpRequestMessage, HttpResponseMessage>? _upload, _report;
    bool _enabled = true;

    string GameDir => Path.Combine(_dir, "games", "steam_480");

    public SharingTests() => Directory.CreateDirectory(GameDir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    /// <summary>A recording as the recorder writes it: shader bytes, a root signature and a pipeline per vertex shader.</summary>
    internal static byte[] LocalRecording(params string[] vsShas)
    {
        var rs = RootSignature();
        using var m = new MemoryStream();
        PsoDb.WriteBlob(m, Sha1(rs), rs);
        PsoDb.WriteBlob(m, Sha1(Shader), Shader);
        foreach (var vs in vsShas.DefaultIfEmpty(Sha1(Shader)))
            PsoDb.Write(m, 'S', PsoDb.Stream(Sha1(rs), new Dictionary<int, string> { [(int)Stage.Vertex] = vs }, [], 3, [PsoDb.R16G16B16A16Float], 0));
        return m.ToArray();
    }

    /// <summary><paramref name="psos"/> distinct pipelines, 1.26 KB each hash-only (real ones average 0.8 KB).</summary>
    static byte[] BigRecording(int psos)
    {
        var rs = RootSignature();
        PsoDb.LayoutElem[] layout = [.. Enumerable.Range(0, 16).Select(i => new PsoDb.LayoutElem("TEXCOORD", i, 2, (uint)i * 16))];
        using var m = new MemoryStream();
        PsoDb.WriteBlob(m, Sha1(rs), rs);
        PsoDb.WriteBlob(m, Sha1(Shader), Shader);
        for (var i = 0; i < psos; i++)
            PsoDb.Write(m, 'S', PsoDb.Stream(Sha1(rs), new Dictionary<int, string> { [(int)Stage.Vertex] = Sha1(BitConverter.GetBytes(i)), [(int)Stage.Pixel] = Sha1(BitConverter.GetBytes(~i)) },
                layout, 3, [PsoDb.R16G16B16A16Float], 0));
        return m.ToArray();
    }

    void Record(byte[] db, int ageMinutes = 0)
    {
        var path = Path.Combine(GameDir, "recording.db");
        File.WriteAllBytes(path, db);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-ageMinutes));
    }

    Sharing Make() => new(_dir, () => _enabled, new RouteFailover(new Fake(Answer), [Com, Io], _clock), _clock);

    HttpResponseMessage Answer(HttpRequestMessage r)
    {
        var body = r.Content?.ReadAsByteArrayAsync().Result ?? [];
        var meta = r.Headers.TryGetValues("X-SCSK-Upload", out var v) ? Encoding.UTF8.GetString(Base64Url.DecodeFromChars(v.Single())) : null;
        lock (_sent) _sent.Add(($"{r.Method} {r.RequestUri}{(r.Headers.Authorization is { } a ? " " + a : "")}", body, meta));
        return r.RequestUri!.AbsolutePath switch
        {
            "/v1/devices" => Ours(HttpStatusCode.OK, """{"device_token":"sd1_test-anon","device_id":"dev1"}"""u8.ToArray()),
            "/v1/upload" => _upload?.Invoke(r) ?? Ours(HttpStatusCode.Accepted, """{"upload_id":"up1","records":1,"new_records":1}"""u8.ToArray()),
            "/v1/report" => _report?.Invoke(r) ?? Ours(HttpStatusCode.NoContent),
            _ => Ours(HttpStatusCode.NotFound),
        };
    }

    static UploadMeta Meta() => new("steam:480@1", Content, "Unreal 4.26", "nvidia", "1.4.0", [new("amdxcffx64.dll", new string('a', 40))]);
    List<string> Uploads => _sent.Where(s => s.Line.StartsWith("POST https://api.test.com/v1/upload")).Select(s => s.Line).ToList();

    [Fact]
    public async Task The_upload_is_hash_only_with_the_anonymous_device_and_never_the_patreon_one()
    {
        // signed in with Patreon: auth.dat holds the member device token, which uploads must never carry
        var auth = Path.Combine(_dir, "auth.dat");
        File.WriteAllBytes(auth, Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(new { Member = Patreon })));
        var authBefore = File.ReadAllBytes(auth);
        Record(LocalRecording());

        var got = await Make().ShareAsync(GameDir, Content, Meta);

        Assert.Equal(("up1", 1, 1), (got!.UploadId, got.Psos, got.NewPsos));
        Assert.Equal(["POST https://api.test.com/v1/devices", $"POST https://api.test.com/v1/upload Bearer {Anon}"], _sent.Select(s => s.Line));
        Assert.DoesNotContain(_sent, s => s.Line.Contains(Patreon));
        Assert.Equal(authBefore, File.ReadAllBytes(auth));   // untouched

        // the body: Brotli of a canonical hash-only recording; Canonical(local: false) is the server's check and throws on shader bytes
        var body = _sent[1].Body;
        var records = Core.Planning.HashOnly.Decompress(body);
        Assert.Equal(records, Core.Planning.HashOnly.Canonical(records, local: false, out _));
        Assert.Equal(["B", "S"], records.Select(r => r.Tag.ToString()));
        Assert.Equal(PsoDb.Hex(System.Security.Cryptography.SHA1.HashData(RootSignature())), PsoDb.Hex(records[0].Payload.AsSpan(0, 20)));
        Assert.DoesNotContain(records, r => r.Payload.AsSpan().IndexOf(Shader) >= 0);

        // the metadata header: the contract's snake_case keys
        var meta = JsonDocument.Parse(_sent[1].Meta!).RootElement;
        Assert.Equal(["store_build_key", "content_hash", "engine", "vendor", "app_version", "middleware"], meta.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("steam:480@1", Content, "amdxcffx64.dll"), (meta.GetProperty("store_build_key").GetString(), meta.GetProperty("content_hash").GetString(),
            meta.GetProperty("middleware")[0].GetProperty("name").GetString()));

        // the upload device lives in its own file, DPAPI-protected, apart from auth.dat
        var dat = File.ReadAllBytes(Path.Combine(_dir, "upload.dat"));
        Assert.Equal(-1, dat.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Anon)));
        Assert.Contains(Anon, Encoding.UTF8.GetString(Dpapi.Unprotect(dat)));
        Assert.Equal("dev1", Make().DeviceId);
    }

    /// <summary>Every recorder tag reaches the upload, ray tracing state objects included; shader and DXIL library bytes don't.</summary>
    [Fact]
    public async Task Ray_tracing_state_objects_are_uploaded_hash_only()
    {
        var local = EveryRecorderTag(out var libSha);
        using (var f = File.Create(Path.Combine(GameDir, "recording.db")))
            foreach (var r in local) PsoDb.Write(f, r.Tag, r.Payload);
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));
        var records = Core.Planning.HashOnly.Decompress(_sent.Single(s => s.Line.Contains("/v1/upload")).Body);
        Assert.Equal(local.Where(r => r.Tag is 'R' or 'A').Select(r => r.Key), records.Where(r => r.Tag is 'R' or 'A').Select(r => r.Key));
        Assert.Equal(Core.Planning.HashOnly.Tags.Replace("D", "").Order(), records.Select(r => r.Tag).Distinct().Order());
        Assert.DoesNotContain(records, r => r.Tag == 'B' && PsoDb.Hex(r.Payload.AsSpan(0, 20)) == libSha);
        Assert.DoesNotContain(records, r => r.Payload.AsSpan().IndexOf(Shader) >= 0);
    }

    /// <summary>Under a layer wrapping the device (a mod), only the game's records are shared: neither the 'W' records, nor
    /// the records the driver got from the layer (changed or its own, with their 'N'), nor root signatures only those name.
    /// The server's check still refuses a 'W'.</summary>
    [Fact]
    public async Task A_layer_s_records_are_never_uploaded()
    {
        var (rs, layerRs) = (RootSignature(), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "layer root signature"));
        var mod = "DXBC the layer's replacement shader"u8.ToArray();
        PsoDb.Rec Vs(byte[] root, byte[] vs) => new('S', PsoDb.Stream(Sha1(root), new Dictionary<int, string> { [(int)Stage.Vertex] = Sha1(vs) }, [], 3, [PsoDb.R16G16B16A16Float], 0));
        var (game, driver, own) = (Vs(rs, Shader), Vs(layerRs, mod), new PsoDb.Rec('C', PsoDb.Compute(Sha1(layerRs), Sha1(mod))));
        using (var f = File.Create(Path.Combine(GameDir, "recording.db")))
            foreach (var r in new[] { Blob(rs), Blob(Shader), game, Blob(layerRs),
                         Blob(mod), driver, new PsoDb.NvState(driver.Key, 0, 1001, 3, 0).ToRec(), RecordingsTests.W(driver, game), own, RecordingsTests.W(own, null) })
                PsoDb.Write(f, r.Tag, r.Payload);

        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));
        var records = Core.Planning.HashOnly.Decompress(_sent.Single(s => s.Line.Contains("/v1/upload")).Body);
        Assert.Equal([Sha1(rs), game.Key], records.Select(r => r.Tag == 'B' ? PsoDb.Hex(r.Payload.AsSpan(0, 20)) : r.Key));
        Assert.Throws<InvalidDataException>(() => Core.Planning.HashOnly.Canonical([.. records, RecordingsTests.W(game, null)], local: false, out _));
    }

    /// <summary>A 'W' names pipeline and state object records only (the recorder writes one per create the layer changed);
    /// root signatures go by what names them. One only the layer's records name is dropped with them; one a game's record
    /// names too is the game's (the layer passed it on unchanged) and is shared; a 'W' naming a root signature's hash names
    /// no record and changes nothing.</summary>
    [Fact]
    public void A_layer_s_root_signatures_go_by_the_records_naming_them()
    {
        var (rs, layerRs) = (RootSignature(), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "layer root signature"));
        PsoDb.Rec Cs(byte[] root, string cs) => new('C', PsoDb.Compute(Sha1(root), Sha1(System.Text.Encoding.UTF8.GetBytes(cs))));
        var (game, driver, passed) = (Cs(rs, "game"), Cs(layerRs, "game"), Cs(rs, "the layer's own on the game's root signature"));
        List<string> Shared(params PsoDb.Rec[] recs) =>
            [.. Core.Planning.HashOnly.Canonical(recs, local: true, out _).Select(r => r.Tag == 'B' ? PsoDb.Hex(r.Payload.AsSpan(0, 20)) : r.Key)];

        Assert.Equal([Sha1(rs), game.Key], Shared(Blob(rs), Blob(layerRs), game, driver, RecordingsTests.W(driver, game)));
        Assert.Equal([Sha1(rs), game.Key], Shared(Blob(rs), game, passed, RecordingsTests.W(passed, null)));
        var named = new PsoDb.Rec('W', [.. Convert.FromHexString(Sha1(rs)), .. new byte[20]]);
        Assert.Equal([Sha1(rs), game.Key], Shared(Blob(rs), game, named));
    }

    static PsoDb.Rec Blob(byte[] b) => new('B', [.. System.Security.Cryptography.SHA1.HashData(b), .. b]);

    /// <summary>With the build's shader list (the index's, <see cref="Sharing.ShippedFile"/>), a pipeline naming a shader
    /// neither it nor a middleware DLL ships is flagged 'L' in the upload; one the DLLs ship isn't. Without the list (or for
    /// another build) nothing is flagged, and the list arriving re-examines the recording.</summary>
    [Fact]
    public async Task A_pipeline_with_a_shader_the_build_doesnt_ship_is_flagged()
    {
        string runtime = Sha1("built at run time"u8.ToArray()), dll = Sha1("in a DLL next to the exe"u8.ToArray());
        Record(LocalRecording(Sha1(Shader), runtime, dll));
        async Task<List<PsoDb.Rec>> Share(bool expectUpload)
        {
            _sent.Clear();
            var got = await Make().ShareAsync(GameDir, Content, Meta, () => [dll]);
            Assert.Equal(expectUpload, got != null);
            return expectUpload ? Core.Planning.HashOnly.Decompress(_sent.Single(s => s.Line.Contains("/v1/upload")).Body) : [];
        }
        Assert.DoesNotContain(await Share(true), r => r.Tag == 'L');

        // both lists are 40 bytes: only their write times tell them apart, and back-to-back writes can share a file-time tick
        var list = Path.Combine(GameDir, Sharing.ShippedFile);
        File.WriteAllBytes(list, [.. Convert.FromHexString("fedcba9876543210fedcba9876543210fedcba98"), .. Convert.FromHexString(Sha1(Shader))]);
        File.SetLastWriteTimeUtc(list, DateTime.UtcNow.AddMinutes(-5));
        await Share(false);   // another build's list: no flags, so the same upload, already sent

        File.WriteAllBytes(list, [.. Convert.FromHexString(Content), .. Convert.FromHexString(Sha1(Shader))]);
        var records = await Share(true);
        var flagged = records.Where(r => r.Tag == 'L').Select(Core.Planning.HashOnly.Target).ToList();
        var pso = Assert.Single(records, r => r.Tag == 'S' && PsoDb.Parse(r).Stages.ContainsValue(runtime));
        Assert.Equal([pso.Key], flagged);
        Assert.Equal(records, Core.Planning.HashOnly.Canonical(records, local: false, out _));
    }

    [Fact]
    public async Task A_recording_is_uploaded_once_per_change_and_the_device_registered_once()
    {
        var sharing = Make();
        Record(LocalRecording("a".PadLeft(40, 'a')), ageMinutes: 10);
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));   // unchanged
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));    // after a restart too (shared.json)

        // the recorder appended shader bytes only: the hash-only form is the same, so nothing new to share
        var db = LocalRecording("a".PadLeft(40, 'a'));
        using (var m = new MemoryStream())
        {
            m.Write(db);
            PsoDb.WriteBlob(m, Sha1("DXBC another shader"u8.ToArray()), "DXBC another shader"u8);
            Record(m.ToArray(), ageMinutes: 5);
        }
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));

        Record(LocalRecording("a".PadLeft(40, 'a'), "b".PadLeft(40, 'b')));   // a new pipeline
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.Single(_sent, s => s.Line.Contains("/v1/devices"));
        Assert.Equal(2, Uploads.Count);
        Assert.NotNull(Sharing.Shared(GameDir)!.At);
    }

    [Fact]
    public async Task Nothing_is_uploaded_that_the_downloaded_entry_of_the_same_build_already_has()
    {
        const string vs = "cccccccccccccccccccccccccccccccccccccccc";
        File.WriteAllBytes(Path.Combine(GameDir, "community.db"), CommunityTests.HashOnly(vs));
        File.WriteAllText(Path.Combine(GameDir, "community.json"), JsonSerializer.Serialize(new CommunityDownload("obj", Content, 1, DateTimeOffset.Now)));
        Record(LocalRecording(vs), ageMinutes: 10);
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Empty(_sent);

        Record(LocalRecording(vs, "dddddddddddddddddddddddddddddddddddddddd"));   // one pipeline the community lacks
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);

        // the same records for another build are new there (the download is of the other build)
        Assert.NotNull(await Make().ShareAsync(GameDir, "fedcba9876543210fedcba9876543210fedcba98", Meta));
        Assert.Equal(2, Uploads.Count);
    }

    [Fact]
    public async Task A_429_backs_off_for_its_retry_after_then_retries()
    {
        var sharing = Make();
        _upload = _ =>
        {
            var r = Ours(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}"""u8.ToArray());
            r.Headers.RetryAfter = new(TimeSpan.FromHours(2));
            return r;
        };
        Record(LocalRecording());
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.NotNull(sharing.Problem);
        _clock.Now += TimeSpan.FromHours(1);
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);   // no request while backing off

        _upload = null;
        _clock.Now += TimeSpan.FromHours(1.1);
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, Meta));   // not marked done by the 429
        Assert.Equal(2, Uploads.Count);
        Assert.Null(sharing.Problem);
    }

    [Fact]
    public async Task A_refused_recording_is_not_retried_and_an_unknown_device_registers_again()
    {
        _upload = _ => Ours(HttpStatusCode.BadRequest, """{"error":"invalid_recording","detail":"x"}"""u8.ToArray());
        Record(LocalRecording(), ageMinutes: 10);
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);

        // the device expired (90 days unused): a new one, once
        var calls = 0;
        _upload = _ => ++calls == 1 ? Ours(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}"""u8.ToArray()) : null!;
        Record(LocalRecording("e".PadLeft(40, 'e')));
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Equal(2, _sent.Count(s => s.Line.Contains("/v1/devices")));
    }

    [Fact]
    public async Task A_long_session_over_the_old_8_MB_is_uploaded_even_after_the_old_silent_skip()
    {
        Record(BigRecording(7_000));
        var db = new FileInfo(Path.Combine(GameDir, "recording.db"));
        File.WriteAllText(Path.Combine(GameDir, "shared.json"),   // a pre-"|2" stamp
            $$"""{"Stamp":"{{db.Length}}:{{db.LastWriteTimeUtc.Ticks}}|{{Content}}","Body":null,"At":null,"UploadId":null,"Psos":0,"NewPsos":0}""");

        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));

        var body = _sent.Single(s => s.Line.Contains("/v1/upload")).Body;
        var records = Core.Planning.HashOnly.Decompress(body);
        Assert.True(records.Sum(r => 5L + r.Payload.Length) > 8 << 20);
        Assert.True(body.Length <= Sharing.MaxBody);
        Assert.Equal(7_000, records.Count(r => r.Tag == 'S'));
    }

    /// <summary>Past one upload's 20k records: several uploads, each valid on its own; a pass cut short by a 429 resumes with
    /// the ones the server doesn't have yet.</summary>
    /// <summary>What a layer made is read again before each upload of a long session: a record learned as a layer's during
    /// the first upload is left out of the second; a list that can't be read then stops the rest, which stays unmarked.</summary>
    [Fact]
    public async Task Each_upload_of_a_long_session_reads_what_a_layer_made_again()
    {
        Record(BigRecording(25_000));
        var last = PsoDb.Read(Path.Combine(GameDir, "recording.db")).Where(r => r.Tag == 'S').Select(r => r.Key).Max(StringComparer.Ordinal)!;   // in the second upload
        var known = new HashSet<string>();
        _upload = _ => { lock (known) known.Add(last); return null!; };
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta, layerMade: () => { lock (known) return new HashSet<string>(known); }));
        var chunks = _sent.Where(s => s.Line.Contains("/v1/upload")).Select(s => Core.Planning.HashOnly.Decompress(s.Body)).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.DoesNotContain(chunks[1], r => r.Key == last);
        Assert.Equal(24_999, chunks.Sum(c => c.Count(r => r.Tag == 'S')));

        File.Delete(Path.Combine(GameDir, "shared.json"));
        _sent.Clear();
        var reads = 0;
        var sharing = Make();
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta, layerMade: () => ++reads <= 2 ? new HashSet<string>() : throw new IOException("locked")));
        Assert.Single(_sent, s => s.Line.Contains("/v1/upload"));
        Assert.Contains("locked", sharing.Problem);
        Assert.Single(Sharing.Shared(GameDir)!.Sent!);   // the first upload, kept; the recording isn't marked shared

        File.Delete(Path.Combine(GameDir, "shared.json"));   // a damaged recording behind the list: the same
        _sent.Clear();
        reads = 0;
        sharing = Make();
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta, layerMade: () => ++reads <= 2 ? new HashSet<string>() : throw new InvalidDataException("damaged")));
        Assert.Single(_sent, s => s.Line.Contains("/v1/upload"));
        Assert.Contains("damaged", sharing.Problem);
        Assert.Single(Sharing.Shared(GameDir)!.Sent!);
        Assert.Equal("", Sharing.Shared(GameDir)!.Stamp);   // not marked shared
    }

    [Fact]
    public async Task A_long_session_goes_in_several_uploads_and_a_pass_cut_short_resumes()
    {
        Record(BigRecording(45_000));   // 57 MB hash-only: over the origin's 32 MB for one upload
        var calls = 0;
        _upload = _ =>
        {
            if (++calls != 2) return null!;
            var r = Ours(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}"""u8.ToArray());
            r.Headers.RetryAfter = new(TimeSpan.FromHours(1));
            return r;
        };
        var sharing = Make();
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.NotNull(sharing.Problem);
        Assert.Single(Sharing.Shared(GameDir)!.Sent!);   // the first one, kept; the recording isn't stamped done

        _clock.Now += TimeSpan.FromHours(1.1);
        var got = await sharing.ShareAsync(GameDir, Content, Meta);
        Assert.Equal(2, got!.Psos);   // the fake's one record per accepted upload: the two left, the first not again
        Assert.Equal(4, Uploads.Count);
        var chunks = _sent.Where(s => s.Line.Contains("/v1/upload")).Select(s => s.Body).Where((_, i) => i != 1)
            .Select(b => Core.Planning.HashOnly.Decompress(b)).ToList();
        foreach (var c in chunks)
        {
            Assert.Equal(c, Core.Planning.HashOnly.Canonical(c, local: false, out _));   // what the server checks, per upload
            Assert.InRange(c.Count(r => r.Tag != 'B'), 1, Sharing.ChunkRecords);
            Assert.True(c.Sum(r => 5L + r.Payload.Length) <= Sharing.ChunkRaw + 4096);
        }
        Assert.Equal(45_000, chunks.SelectMany(c => c.Where(r => r.Tag == 'S').Select(r => r.Key)).Distinct().Count());
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));   // done
        Assert.Equal(4, Uploads.Count);
    }

    [Fact]
    public async Task A_blocked_device_stops_the_pass_and_the_recording_is_done_until_it_changes()
    {
        _upload = _ => Ours(HttpStatusCode.Forbidden, """{"error":"blocked"}"""u8.ToArray());
        Record(BigRecording(25_000), ageMinutes: 10);   // two uploads' worth
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);
        Assert.Single(_sent, s => s.Line.Contains("/v1/devices"));   // a 403 is not a 401: no new device
    }

    /// <summary>D3D11 draws go in their own upload under the build's draws key, never in the game build's; a server that refuses
    /// that key gets no draws again until another app version, and the game's pipelines still go.</summary>
    [Fact]
    public async Task D3D11_draws_go_apart_under_the_draws_key_and_a_refusal_holds_until_the_next_version()
    {
        static PsoDb.Rec Draw(string vs) => new('D', PsoDb.Draw11Bytes(new(vs, PsoDb.Zero, PsoDb.Zero, PsoDb.Zero, 4, true, [new("POSITION", 0, 6, 0)])));
        void Mixed(params string[] vss)
        {
            using var m = new MemoryStream();
            m.Write(LocalRecording(vss));
            foreach (var vs in vss) PsoDb.Write(m, 'D', Draw(vs).Payload);
            PsoDb.Write(m, 'D', Draw(vs: new string('8', 40)).Payload[..^24]);   // an element cut off: dropped, the rest still go
            Record(m.ToArray());
        }
        var key = Core.Planning.HashOnly.DrawsKey(Content);
        File.WriteAllBytes(Path.Combine(GameDir, Sharing.ShippedFile), Convert.FromHexString(Content + new string('a', 40) + new string('9', 40)));   // flags run over the draws
        // a draws-only recording an earlier version stamped as done goes now
        using (var only = new MemoryStream())
        {
            PsoDb.Write(only, 'D', Draw(new string('9', 40)).Payload);
            Record(only.ToArray(), ageMinutes: 10);
        }
        var rec = new FileInfo(Path.Combine(GameDir, "recording.db"));
        File.WriteAllText(Path.Combine(GameDir, "shared.json"), JsonSerializer.Serialize(new SharedRecording($"{rec.Length}:{rec.LastWriteTimeUtc.Ticks}|{Content}|2")));
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);
        _sent.Clear();

        Mixed(new string('a', 40));
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));
        var ups = _sent.Where(s => s.Line.Contains("/v1/upload")).ToList();
        Assert.Equal(2, ups.Count);
        Assert.Equal("steam:480@1", JsonDocument.Parse(ups[0].Meta!).RootElement.GetProperty("store_build_key").GetString());
        Assert.DoesNotContain(Core.Planning.HashOnly.Decompress(ups[0].Body), r => r.Tag == 'D');
        var meta = JsonDocument.Parse(ups[1].Meta!).RootElement;
        Assert.Equal((key, Core.Planning.HashOnly.PackHash(key)), (meta.GetProperty("store_build_key").GetString(), meta.GetProperty("content_hash").GetString()));
        Assert.Equal([Draw(new string('a', 40)).Key], Core.Planning.HashOnly.CheckDraws(Core.Planning.HashOnly.Decompress(ups[1].Body)).Select(r => r.Key));

        // a server without draws entries: refused quietly, remembered for this version
        _upload = r => JsonDocument.Parse(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(r.Headers.GetValues("X-SCSK-Upload").Single()))).RootElement
            .GetProperty("store_build_key").GetString() == key ? Ours(HttpStatusCode.BadRequest, """{"error":"bad_request"}"""u8.ToArray()) : null!;
        var sharing = Make();
        Mixed(new string('b', 40));
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.Null(sharing.Problem);
        Assert.Equal(AppVersion.Current.ToString(), File.ReadAllText(Path.Combine(_dir, Sharing.DrawsRefusedFile)));
        _sent.Clear();
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));
        Mixed(new string('c', 40));
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);   // the pipelines only

        // another version tries the draws again, without a change of the recording
        File.WriteAllText(Path.Combine(_dir, Sharing.DrawsRefusedFile), "0.0.1");
        _upload = null;
        _sent.Clear();
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Equal(key, JsonDocument.Parse(_sent.Single(s => s.Line.Contains("/v1/upload")).Meta!).RootElement.GetProperty("store_build_key").GetString());
    }

    [Fact]
    public async Task Sharing_turned_off_during_a_pass_sends_nothing_more()
    {
        Record(BigRecording(25_000));   // two uploads' worth
        _upload = _ =>
        {
            _enabled = false;   // the user turns it off while the first upload is on its way
            return null!;
        };
        var sharing = Make();
        Assert.Null(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);
        Assert.Single(Sharing.Shared(GameDir)!.Sent!);   // the one sent is kept; the recording isn't stamped done

        _enabled = true;
        _upload = null;
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, Meta));
        Assert.Equal(2, Uploads.Count);   // the rest, once it is on again
    }

    [Fact]
    public async Task Sharing_turned_off_before_the_retry_of_a_401_sends_it_no_more()
    {
        Record(LocalRecording());
        _upload = _ =>
        {
            _enabled = false;
            return Ours(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}"""u8.ToArray());
        };
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Single(Uploads);
        Assert.Single(_sent, s => s.Line.Contains("/v1/devices"));   // no new device either
        Assert.Null(Sharing.Shared(GameDir));
    }

    [Fact]
    public async Task Sharing_off_sends_nothing_and_registers_nothing()
    {
        _enabled = false;
        Record(LocalRecording());
        Assert.Null(await Make().ShareAsync(GameDir, Content, Meta));
        Assert.Empty(_sent);
        Assert.False(File.Exists(Path.Combine(_dir, "upload.dat")));
        Assert.Null(Sharing.Shared(GameDir));
    }

    /// <summary>A recording that has a layer's pipeline without its 'W' (from before the recorder wrote one) uploads without
    /// it once another recording's 'W' named it (<see cref="MiddlewarePacks.LayerMade"/>), and without its root signature.</summary>
    [Fact]
    public async Task A_recording_upload_leaves_out_what_another_recording_knows_a_layer_made()
    {
        var (rs, layerRs) = (RootSignature(), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "layer root signature"));
        var (game, layer) = (new PsoDb.Rec('C', PsoDb.Compute(Sha1(rs), Sha1(Shader))), new PsoDb.Rec('C', PsoDb.Compute(Sha1(layerRs), Sha1(Shader))));
        using (var f = File.Create(Path.Combine(GameDir, "recording.db")))
            foreach (var r in new[] { Blob(rs), Blob(Shader), game, Blob(layerRs), layer })
                PsoDb.Write(f, r.Tag, r.Payload);

        await Assert.ThrowsAsync<IOException>(() => Make().ShareAsync(GameDir, Content, Meta, layerMade: () => throw new IOException("locked")));
        Assert.Empty(_sent.Where(s => s.Line.Contains("/v1/upload")));   // the list unreadable: nothing shared, and not marked shared
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, Meta, layerMade: () => new HashSet<string> { layer.Key }));
        var records = Core.Planning.HashOnly.Decompress(_sent.Single(s => s.Line.Contains("/v1/upload")).Body);
        Assert.Equal([Sha1(rs), game.Key], records.Select(r => r.Tag == 'B' ? PsoDb.Hex(r.Payload.AsSpan(0, 20)) : r.Key));
    }

    /// <summary>A pack's upload leaves out the records a layer made and the root signature only they name, whatever the pack
    /// file still holds (a share between a recording's import and the packs' exclusion, or after a crash there). The list is
    /// read again before each pack's payload: one learned during the pass applies to the next pack. A list that can't be read
    /// ends the pass with nothing shared.</summary>
    [Fact]
    public async Task A_pack_upload_leaves_out_a_layer_s_records_the_pack_still_holds()
    {
        var packs = Path.Combine(_dir, "packs");
        var (rs, layerRs) = (RootSignature(), SCSKiller.Tests.Planning.MiddlewarePackTests.Container("RTS0", "layer root signature"));
        var (game, layer) = (new PsoDb.Rec('C', PsoDb.Compute(Sha1(rs), Sha1(Shader))), new PsoDb.Rec('C', PsoDb.Compute(Sha1(layerRs), Sha1(Shader))));
        List<string> paths = [];
        foreach (var dllSha in new[] { new string('d', 40), new string('e', 40) })
        {
            var fsr = new MiddlewarePack("amd", "amd_fidelityfx_dx12.dll", dllSha, 1000, gpu: "nvidia");
            (fsr.RootSignatures[Sha1(rs)], fsr.RootSignatures[Sha1(layerRs)]) = (rs, layerRs);
            fsr.Add(game, "steam:480");
            fsr.Add(layer, "steam:480");
            paths.Add(Path.Combine(packs, "amd", MiddlewarePack.FileName(fsr.Header.Dll, dllSha)));
            fsr.Write(paths[^1]);
        }
        var sharing = Make();
        var known = new HashSet<string>();
        IReadOnlySet<string> Known() { var now = new HashSet<string>(known); known.Add(layer.Key); return now; }   // learned after the first read
        Assert.Equal(2, (await sharing.SharePacksAsync(packs, "nvidia", "1.4.0", Known)).Count);
        var bodies = _sent.Where(s => s.Line.Contains("/v1/upload")).Select(s => Core.Planning.HashOnly.Decompress(s.Body)
            .Select(r => r.Tag == 'B' ? PsoDb.Hex(r.Payload.AsSpan(0, 20)) : r.Key).ToList()).ToList();
        Assert.Equal(3 + 1, bodies[0].Count);   // the first pack: both root signatures and both pipelines
        Assert.Equal([Sha1(rs), game.Key], bodies[1]);

        var more = MiddlewarePack.Read(paths[0]);
        more.Add(new PsoDb.Rec('C', PsoDb.Compute(Sha1(rs), Sha1("another"u8.ToArray()))), "steam:480");
        more.Write(paths[0]);
        Assert.Empty(await sharing.SharePacksAsync(packs, "nvidia", "1.4.0", () => throw new IOException("locked")));
        Assert.Equal(2, _sent.Count(s => s.Line.Contains("/v1/upload")));
        Assert.Equal("locked", sharing.Problem);
    }

    [Fact]
    public async Task Upscaler_packs_upload_under_their_pack_key_once_per_change_and_only_for_the_shared_vendors()
    {
        var packs = Path.Combine(_dir, "packs");
        var rs = RootSignature();
        var dllSha = new string('d', 40);
        var fsr = new MiddlewarePack("amd", "amd_fidelityfx_dx12.dll", dllSha, 1000, gpu: "nvidia");
        fsr.RootSignatures[Sha1(rs)] = rs;
        fsr.Add(new PsoDb.Rec('C', PsoDb.Compute(Sha1(rs), Sha1(Shader))), "steam:480");
        var path = Path.Combine(packs, "amd", MiddlewarePack.FileName(fsr.Header.Dll, dllSha));
        fsr.Write(path);
        var opti = new MiddlewarePack("optiscaler", "OptiScaler.dll", dllSha, 1000, gpu: "nvidia");   // stays on this PC
        opti.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, Sha1(Shader))), "steam:480");
        opti.Write(Path.Combine(packs, "optiscaler", MiddlewarePack.FileName(opti.Header.Dll, dllSha)));
        var sharing = Make();

        Assert.Equal([("amd_fidelityfx_dx12.dll", 1)], await sharing.SharePacksAsync(packs, "nvidia", "1.4.0"));
        var up = _sent.Single(s => s.Line.Contains("/v1/upload"));
        Assert.EndsWith(" Bearer " + Anon, up.Line);
        var key = $"nvidia:amd:amd_fidelityfx_dx12.dll:{dllSha}";
        using (var meta = JsonDocument.Parse(up.Meta!))
            Assert.Equal((key, Core.Planning.HashOnly.PackHash(key), "nvidia"),
                (meta.RootElement.GetProperty("store_build_key").GetString(), meta.RootElement.GetProperty("content_hash").GetString(), meta.RootElement.GetProperty("vendor").GetString()));
        Assert.Equal("BC", string.Concat(Core.Planning.HashOnly.Decompress(up.Body).Select(r => r.Tag)));   // hash-only: the root signature and the PSO
        Assert.True(up.Body.AsSpan().IndexOf(Shader) < 0);

        Assert.Empty(await sharing.SharePacksAsync(packs, "nvidia", "1.4.0"));   // unchanged: not again
        fsr.Add(new PsoDb.Rec('C', PsoDb.Compute(Sha1(rs), Sha1("another"u8.ToArray()))), "steam:480");
        fsr.Write(path);
        Assert.Single(await sharing.SharePacksAsync(packs, "nvidia", "1.4.0"));   // it gained a record
        Assert.Equal(2, Uploads.Count);

        // a pack another GPU vendor filled (this PC before a GPU change), or one from before packs kept their GPU: not shared
        var xess = new MiddlewarePack("intel", "libxess.dll", dllSha, 1000, gpu: "amd");
        xess.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, Sha1(Shader))), "steam:480");
        xess.Write(Path.Combine(packs, "intel", MiddlewarePack.FileName(xess.Header.Dll, dllSha)));
        var old = new MiddlewarePack("amd", "ffx_fsr2_api_dx12_x64.dll", dllSha, 1000);
        old.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, Sha1(Shader))), "steam:480");
        old.Write(Path.Combine(packs, "amd", MiddlewarePack.FileName(old.Header.Dll, dllSha)));
        Assert.Empty(await sharing.SharePacksAsync(packs, "nvidia", "1.4.0"));
        Assert.Equal(2, Uploads.Count);

        _enabled = false;
        File.Delete(Path.Combine(_dir, "packs-shared.json"));
        Assert.Empty(await sharing.SharePacksAsync(packs, "nvidia", "1.4.0"));
        Assert.Equal(2, Uploads.Count);

        // turned off while the first of two changed packs is on its way: not the second
        var fsr2 = new MiddlewarePack("amd", "amd_fidelityfx_dx12.dll", new string('e', 40), 1000, gpu: "nvidia");
        fsr2.Add(new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, Sha1(Shader))), "steam:480");
        fsr2.Write(Path.Combine(packs, "amd", MiddlewarePack.FileName(fsr2.Header.Dll, fsr2.Header.ContentHash)));
        File.Delete(Path.Combine(_dir, "packs-shared.json"));
        _upload = _ =>
        {
            _enabled = false;
            return null!;
        };
        _enabled = true;
        await sharing.SharePacksAsync(packs, "nvidia", "1.4.0");
        Assert.Equal(3, Uploads.Count);
    }

    static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public async Task The_plans_rule_check_rides_in_the_upload_header_and_is_left_out_when_unknown()
    {
        Record(LocalRecording());
        var meta = Meta() with { RsRule = "Ue426", BuiltOk = 994, BuiltN = 994, PlannerVersion = 26, DriverVersion = "610.88", GpuModel = "NVIDIA GeForce Test" };
        Assert.NotNull(await Make().ShareAsync(GameDir, Content, () => meta));
        var json = _sent.Single(s => s.Line.Contains("/v1/upload")).Meta!;
        Assert.Equal(["store_build_key", "content_hash", "engine", "vendor", "app_version", "middleware", "rs_rule", "built_ok", "built_n", "planner_version",
            "driver_version", "gpu_model"],
            JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name));
        var back = JsonSerializer.Deserialize<UploadMeta>(json, SnakeCase)!;
        Assert.Equal(meta.Middleware, back.Middleware);
        Assert.Equal(meta, back with { Middleware = meta.Middleware });
        // the header without them is the one older servers parse (The_upload_is_hash_only_... checks its members)
        Assert.DoesNotContain("rs_rule", JsonSerializer.Serialize(Meta(), SnakeCase));
    }

    static string K(int n) => n.ToString("x40");
    static string B64(params int[] keys) => Base64Url.EncodeToString([.. keys.SelectMany(k => Convert.FromHexString(K(k)))]);
    static readonly DateTimeOffset Warmed = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    string Csv(params string[] lines)
    {
        var path = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void The_report_holds_the_first_launch_after_the_warm_its_compiles_counted_and_their_keys_by_ms()
    {
        var at = Warmed.ToUnixTimeMilliseconds();
        var csv = Csv(
            $"#session,{at - 60_000},Game.exe", $"1.0,G,0,0,500.0,{K(9)}", $"#end,{at - 1000}",   // before the warm
            $"#session,{at + 1000},Launcher.exe", $"1.0,G,0,0,500.0,{K(8)}", $"#end,{at + 1500}",   // another exe of the folder
            $"#session,{at + 2000},GAME.EXE", "#clock,0.0",
            $"1.0,g,0,0,300.0,{K(7)}",    // the game's own pipeline library
            $"2.0,G,0,0,1.0,{K(6)}",      // a driver cache hit
            $"3.0,G,0,0,5.0,{K(1)}",
            $"4.0,C,0,0,20.0,{K(2)}",
            $"5.0,S,1,1,80.0,{K(3)}",     // recorded, still compiled: known isn't "in the plan"
            $"6.0,G,0,0,250.0,{K(4)}",
            $"7.0,G,0,0,4.0,{K(4)}",      // the same key again: counted, its slowest create kept
            $"8.0,R,0,0,90.0,{K(5)}",     // a state object: a cached one can take 25 ms
            "9.0,G,0,0,40.0",             // an older proxy's row without a key: counted only
            $"#end,{at + 60_000}",
            $"#session,{at + 70_000},Game.exe", $"1.0,G,0,0,999.0,{K(10)}");

        var r = OutcomeReport.Build(csv, "Game.exe", Warmed, null, null, About)!;

        Assert.Equal(About with { Cold = 6, Keys = r.Keys }, r);
        Assert.Equal([B64(1), B64(2), B64(3), B64(4)], r.Keys!);
        var json = JsonSerializer.Serialize(r, SnakeCase);
        Assert.Equal(["content_hash", "vendor", "gpu_model", "driver_version", "app_version", "rs_rule", "planner_version", "cold", "keys"], JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void The_report_waits_for_the_launch_to_end_and_keeps_every_key_up_to_the_ceiling()
    {
        var at = Warmed.ToUnixTimeMilliseconds();
        string[] creates = [.. Enumerable.Range(0, OutcomeReport.MaxKeys + 1).Select(i => FormattableString.Invariant($"{i}.0,G,0,0,{4 + i * 0.1:0.0},{K(i)}"))];
        var csv = Csv([$"#session,{at + 1000},game.exe", .. creates]);
        Assert.Null(OutcomeReport.Build(csv, "game.exe", Warmed, null, null, About));   // still running, or not seen to exit
        var played = new PlayWindow(Warmed, Warmed.AddHours(1));
        var r = OutcomeReport.Build(csv, "game.exe", Warmed, played, null, About)!;
        Assert.Equal(OutcomeReport.MaxKeys + 1, r.Cold);
        var kept = r.Keys!.SelectMany(k => Base64Url.DecodeFromChars(k).Chunk(20)).Select(Convert.ToHexStringLower).ToList();
        Assert.Equal(OutcomeReport.MaxKeys, kept.Distinct().Count());
        Assert.DoesNotContain(K(0), kept);   // the fastest
        Assert.Equal([59, 400, 1500, 98_041], r.Keys!.Select(k => Base64Url.DecodeFromChars(k).Length / 20));   // 4.1-9.9 ms, 10-49.9, 50-199.9, 200 and over
        Assert.Equal(K(OutcomeReport.MaxKeys), Convert.ToHexStringLower(Base64Url.DecodeFromChars(r.Keys![3])[..20]));   // slowest first
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(r, SnakeCase).Length, 2_600_000, 4 << 20);   // the edge's cap
        Assert.Null(OutcomeReport.Build(Csv(), "game.exe", Warmed, played, null, About));
        Assert.Null(OutcomeReport.Build(csv, "game.exe", Warmed, played, null, About, minCreates: OutcomeReport.MaxKeys + 2));   // too short a launch

        // the 2,000 slowest an older server takes: all of the 200 ms bucket's first, slowest first
        var old = r.Slowest(OutcomeReport.OldMaxKeys);
        Assert.Equal(["", "", "", B64([.. Enumerable.Range(0, 2000).Select(i => OutcomeReport.MaxKeys - i)])], old.Keys!);
        Assert.Equal(r with { Keys = old.Keys }, old);
        Assert.Equal([0, 10, 1500, 98_041], r.Slowest(98_041 + 1500 + 10).Keys!.Select(k => Base64Url.DecodeFromChars(k).Length / 20));
        Assert.Equal(B64(459, 458), r.Slowest(98_041 + 1500 + 2).Keys![1]);
    }

    static readonly OutcomeReport About = new(Content, "nvidia", "NVIDIA GeForce Test", "610.88", "1.3.0", "Ue426", 26);
    static OutcomeReport Report() => About with { Cold = 1, Keys = [B64(1), "", "", ""] };
    string Stamp => Path.Combine(GameDir, Sharing.ReportedFile);

    [Fact]
    public async Task The_report_goes_once_per_warm_without_any_token_and_never_with_sharing_off()
    {
        var built = 0;
        OutcomeReport? Build() { built++; return Report(); }
        _enabled = false;
        Assert.False(await Make().ReportAsync(GameDir, "warm1", Build));
        Assert.Equal((0, 0), (built, _sent.Count));

        _enabled = true;
        var sharing = Make();
        Assert.True(await sharing.ReportAsync(GameDir, "warm1", Build));
        // no upload device, no token: nothing to link a report to
        Assert.Equal(["POST https://api.test.com/v1/report"], _sent.Select(s => s.Line));
        Assert.False(File.Exists(Path.Combine(_dir, "upload.dat")));
        Assert.Equal(JsonSerializer.Serialize(Report(), SnakeCase), Encoding.UTF8.GetString(_sent[0].Body));
        Assert.Null(_sent[0].Meta);
        Assert.False(await sharing.ReportAsync(GameDir, "warm1", Build));
        Assert.False(await Make().ReportAsync(GameDir, "warm1", Build));   // after a restart too
        Assert.Equal((1, 1), (built, _sent.Count));
        Assert.True(await sharing.ReportAsync(GameDir, "warm2", Build));   // the next warm's
        Assert.Equal((2, 2), (built, _sent.Count));
        Assert.False(await sharing.ReportAsync(GameDir, "warm3", () => null));   // its first launch hasn't ended: nothing sent, not stamped
        Assert.Equal(("warm2", 2), (File.ReadAllText(Stamp), _sent.Count));
    }

    /// <summary>A report of <paramref name="n"/> keys: half at 200 ms and over, half at 50-200.</summary>
    static OutcomeReport ReportOf(int n) => About with { Cold = n, Keys = ["", "", B64([.. Enumerable.Range(n / 2, n - n / 2)]), B64([.. Enumerable.Range(0, n / 2)])] };

    static int KeysIn(byte[] body) => JsonDocument.Parse(body).RootElement.GetProperty("keys").EnumerateArray().Sum(k => Base64Url.DecodeFromChars(k.GetString()).Length / 20);

    [Fact]
    public async Task Every_key_goes_in_one_report()
    {
        Assert.True(await Make().ReportAsync(GameDir, "warm1", () => ReportOf(5000)));
        Assert.Equal(5000, KeysIn(Assert.Single(_sent).Body));
    }

    [Theory]
    [InlineData(2400)]    // under 64 KB: the older origin's 400
    [InlineData(5000)]    // over it: the older edge's 413
    public async Task An_older_server_gets_the_2000_slowest_keys_once_more(int n)
    {
        // a server from before every key was sent
        _report = r =>
        {
            var body = r.Content!.ReadAsByteArrayAsync().Result;
            return body.Length > 64 << 10 ? Ours(HttpStatusCode.RequestEntityTooLarge)
                : KeysIn(body) > 2000 ? Ours(HttpStatusCode.BadRequest) : Ours(HttpStatusCode.NoContent);
        };
        Assert.True(await Make().ReportAsync(GameDir, "warm1", () => ReportOf(n)));
        Assert.Equal([n, 2000], _sent.Select(s => KeysIn(s.Body)));
        Assert.Equal(JsonSerializer.Serialize(ReportOf(n).Slowest(2000), SnakeCase), Encoding.UTF8.GetString(_sent[1].Body));
        Assert.Equal("warm1", File.ReadAllText(Stamp));
    }

    [Fact]
    public async Task A_refused_report_goes_once_more_at_most_and_only_when_it_had_more_than_2000_keys()
    {
        _report = _ => Ours(HttpStatusCode.BadRequest);
        Assert.False(await Make().ReportAsync(GameDir, "warm1", () => ReportOf(2000)));
        Assert.Single(_sent);
        Assert.False(await Make().ReportAsync(GameDir, "warm2", () => ReportOf(3000)));
        Assert.Equal([2000, 3000, 2000], _sent.Select(s => KeysIn(s.Body)));
        Assert.Equal("warm2", File.ReadAllText(Stamp));   // refused for good
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task A_server_without_the_report_route_is_not_sent_to_quietly_and_asked_again_a_day_later(HttpStatusCode code)
    {
        var sharing = Make();
        _report = _ => Ours(code);
        Assert.False(await sharing.ReportAsync(GameDir, "warm1", Report));
        Assert.Null(sharing.Problem);
        Assert.False(File.Exists(Stamp));
        _clock.Now += TimeSpan.FromHours(23);
        Assert.False(await sharing.ReportAsync(GameDir, "warm1", Report));
        Assert.Single(_sent, s => s.Line.Contains("/v1/report"));

        Record(LocalRecording());   // uploads aren't held back by it
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, Meta));

        _report = null;
        _clock.Now += TimeSpan.FromHours(1);
        Assert.True(await sharing.ReportAsync(GameDir, "warm1", Report));
        Assert.True(File.Exists(Stamp));
    }

    [Fact]
    public async Task A_new_rule_check_goes_with_one_pipeline_when_nothing_else_is_new()
    {
        Record(LocalRecording("a".PadLeft(40, 'a'), "b".PadLeft(40, 'b')), ageMinutes: 10);
        var sharing = Make();
        UploadMeta With(string check) => Meta() with { RsRule = "Ue426", BuiltOk = 3, BuiltN = 3, PlannerVersion = check == "v1" ? 26 : 27 };
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, () => With("v1"), planCheck: "v1"));
        Assert.Equal(2, Core.Planning.HashOnly.Decompress(_sent.Single(s => s.Line.Contains("/v1/upload")).Body).Count(r => r.Tag != 'B'));
        _sent.Clear();
        Assert.Null(await sharing.ShareAsync(GameDir, Content, () => With("v1"), planCheck: "v1"));
        Assert.Empty(_sent);

        // the recording is the same, the plan's check isn't: the smallest valid upload carries it
        Assert.NotNull(await sharing.ShareAsync(GameDir, Content, () => With("v2"), planCheck: "v2"));
        var upload = _sent.Single(s => s.Line.Contains("/v1/upload"));
        var records = Core.Planning.HashOnly.Decompress(upload.Body);
        Assert.Equal(["B", "S"], records.Select(r => r.Tag.ToString()));
        Assert.Equal(records, Core.Planning.HashOnly.Canonical(records, local: false, out _));
        Assert.Contains("\"planner_version\":27", upload.Meta);
        _sent.Clear();
        Assert.Null(await sharing.ShareAsync(GameDir, Content, () => With("v2"), planCheck: "v2"));
        Assert.Null(await Make().ShareAsync(GameDir, Content, () => With("v2"), planCheck: "v2"));
        Assert.Empty(_sent);
    }

    /// <summary>A plan names its root-signature rule by its id in plan-rules.json (PlanStats.RsRule): every id of the
    /// embedded file is one origin's reports.rs rule_id takes.</summary>
    [Fact]
    public void Every_plan_rule_id_is_one_the_server_takes_as_rs_rule()
    {
        var ids = System.Text.Json.Nodes.JsonNode.Parse(ContentFile.Embedded("SCSKiller.Core.Planning.plan-rules.json"))!["rules"]!.AsArray()
            .Select(r => r!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(RulesFile.Embedded.Rules.Select(r => r.Id), ids);
        Assert.All(ids, id => Assert.Matches("^[a-z0-9][a-z0-9.-]{0,31}$", id));
    }
}
