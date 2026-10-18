using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Platform;

// The community database's read side against a fake handler: no network, synthetic recordings only.
public class CommunityTests : IDisposable
{
    static readonly Uri Com = new("https://api.test.com/"), Io = new("https://api.test.io/");
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-community-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Clock _clock = new();

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;   // the monotonic clock moves with Now
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    internal sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<string> Log = [];   // "GET https://api.test.com/v1/x [bytes=80-] [Bearer t]"
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            lock (Log) Log.Add($"{r.Method} {r.RequestUri}{(r.Headers.Range is { } range ? " " + range : "")}{(r.Headers.Authorization is { } a ? " " + a : "")}");
            return Task.FromResult(answer(r));
        }
    }

    internal static HttpResponseMessage Ours(HttpStatusCode code, byte[]? body = null)
    {
        var r = new HttpResponseMessage(code) { Content = new ByteArrayContent(body ?? []) };
        r.Headers.Add("X-SCSK", "1");
        return r;
    }

    internal static byte[] Record(char kind, Action<Span<byte>> fill)
    {
        var r = new byte[CommunityManifest.RecordSize];
        r[0] = (byte)kind;
        fill(r);
        return r;
    }
    internal static byte[] Header(uint epoch = 1) => Record('M', r => { r[1] = 1; BinaryPrimitives.WriteUInt32LittleEndian(r[4..], epoch); });
    internal static byte[] Entry(string contentHash, byte[] obj, int psos, ushort uploaders = 2) => Record('E', r =>
    {
        BinaryPrimitives.WriteUInt16LittleEndian(r[2..], uploaders);
        Convert.FromHexString(contentHash).CopyTo(r[4..]);
        SHA256.HashData(obj).CopyTo(r[24..]);
        BinaryPrimitives.WriteUInt32LittleEndian(r[56..], (uint)obj.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(r[60..], (uint)psos);
    });
    internal static byte[] Full(string contentHash, byte[] obj, int psos, ushort uploaders = 2) => [(byte)'X', .. Entry(contentHash, obj, psos, uploaders).AsSpan(1)];
    internal static byte[] Alias(string storeBuildKey, string contentHash) => Record('A', r =>
    {
        SHA1.HashData(Encoding.UTF8.GetBytes(storeBuildKey)).CopyTo(r[4..]);
        Convert.FromHexString(contentHash).CopyTo(r[24..]);
    });
    internal static byte[] Tombstone(string contentHash) => Record('T', r => Convert.FromHexString(contentHash).CopyTo(r[4..]));
    internal static byte[] Official(string contentHash, byte[] obj, int psos, OfficialVisibility visibility) => Record('O', r =>
    {
        r[1] = (byte)visibility;
        BinaryPrimitives.WriteUInt16LittleEndian(r[2..], 1);
        Convert.FromHexString(contentHash).CopyTo(r[4..]);
        SHA256.HashData(obj).CopyTo(r[24..]);
        BinaryPrimitives.WriteUInt32LittleEndian(r[56..], (uint)obj.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(r[60..], (uint)psos);
    });
    internal static byte[] Manifest(params byte[][] records) => [.. Header(), .. records.SelectMany(r => r)];

    internal static string Sha1(byte[] b) => PsoDb.Hex(SHA1.HashData(b));

    /// <summary>A root signature as the database carries it: a DXBC container of one RTS0 part (made-up bytes), padded to
    /// <paramref name="size"/>.</summary>
    internal static byte[] RootSignature(int size = 68)
    {
        var c = new byte[size];
        "DXBC"u8.CopyTo(c);
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(24), (uint)c.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(32), 36);
        "RTS0"u8.CopyTo(c.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(40), (uint)size - 44);
        // an empty 1.0 root signature: no parameters, no static samplers
        foreach (var (at, v) in new[] { (44, 1u), (52, 24u), (60, 24u) }) BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(at), v);
        return c;
    }

    /// <summary>A hash-only recording: the root signature's 'B' and one pipeline stream naming <paramref name="vsSha"/>.</summary>
    internal static byte[] HashOnly(string vsSha)
    {
        var rs = RootSignature();
        using var m = new MemoryStream();
        PsoDb.WriteBlob(m, Sha1(rs), rs);
        PsoDb.Write(m, 'S', PsoDb.Stream(Sha1(rs), new Dictionary<int, string> { [(int)Stage.Vertex] = vsSha }, [], 3, [PsoDb.R16G16B16A16Float], 0));
        return m.ToArray();
    }

    /// <summary>A recording's records as a proxy db.</summary>
    static byte[] RawOf(string db)
    {
        using var m = new MemoryStream();
        foreach (var r in PsoDb.Read(db)) PsoDb.Write(m, r.Tag, r.Payload);
        return m.ToArray();
    }

    /// <summary><see cref="Community.Check"/>, asserted to give what a download's check as it decodes gives
    /// (<see cref="Community.CheckPacked"/>), which keeps the object as it came.</summary>
    static int? Check(byte[] raw)
    {
        var psos = Community.Check(raw);
        var packed = Brotli(raw);
        Assert.Equal(psos, Community.CheckPacked(packed, out var compact));
        if (compact != null) Assert.Equal(PsoDb.Compact(raw.Length, packed), compact);
        return psos;
    }

    internal static byte[] Brotli(byte[] raw)
    {
        using var m = new MemoryStream();
        using (var b = new BrotliStream(m, CompressionLevel.Optimal)) b.Write(raw);
        return m.ToArray();
    }

    const string Content = "0123456789abcdef0123456789abcdef01234567";
    static readonly Game Game = new("steam:480", "Spacewar", Store.Steam, @"C:\nowhere", @"C:\nowhere\x.exe", "1");

    Community Make(Fake fake, string? token = "t", Func<bool, string?>? tokens = null) =>
        new(_dir, (fresh, _) => Task.FromResult(tokens != null ? tokens(fresh) : token), new RouteFailover(fake, [Com, Io], _clock), _clock);

    [Fact]
    public void The_manifest_replays_entries_aliases_and_tombstones_in_order()
    {
        var (a, b, c) = ("a"u8.ToArray(), "b"u8.ToArray(), "c"u8.ToArray());
        const string other = "fedcba9876543210fedcba9876543210fedcba98";
        var m = CommunityManifest.Parse(Manifest(Entry(Content, a, 10), Alias("steam:480@1", Content), Entry(Content, b, 20, 3),
            Entry(other, c, 5), Tombstone(other), Record('Z', _ => { })));   // an unknown kind is skipped
        Assert.Equal(1u, m.Epoch);
        var e = m.Find(Game, null)!;   // by the store build
        Assert.Equal((Content, PsoDb.Hex(SHA256.HashData(b)), 1L, 20, 3), (e.ContentHash, e.Object, e.Size, e.Psos, e.Uploaders));   // the last 'E' wins
        Assert.Same(e, m.Find(Game with { Version = "2" }, Content));   // by content hash (a fresh index)
        Assert.Null(m.Find(Game with { Version = "2" }, null));
        Assert.Null(m.Find(Game with { Version = null }, null));
        Assert.Null(m.Find(Game, other));   // withdrawn
        Assert.Equal(Sha1(Encoding.UTF8.GetBytes("steam:480@1")), CommunityManifest.AliasKey(Game));
        Assert.Throws<InvalidDataException>(() => CommunityManifest.Parse(new byte[81]));
        Assert.Throws<InvalidDataException>(() => CommunityManifest.Parse(Entry(Content, a, 1)));   // no header
    }

    [Fact]
    public void An_entrys_full_cut_is_found_only_when_asked_for_and_a_tombstone_withdraws_both_cuts()
    {
        var (a, b, c, d) = ("a"u8.ToArray(), "b"u8.ToArray(), "c"u8.ToArray(), "d"u8.ToArray());
        string Obj(byte[] o) => PsoDb.Hex(SHA256.HashData(o));
        var m = CommunityManifest.Parse(Manifest(Entry(Content, a, 10), Alias("steam:480@1", Content), Full(Content, b, 20), Full(Content, c, 30, 4),
            Record('Y', _ => { })));   // an unknown kind is skipped
        var x = m.Find(Game, null, full: true)!;   // by the store build
        Assert.Equal((Content, Obj(c), 1L, 30, 4, true), (x.ContentHash, x.Object, x.Size, x.Psos, x.Uploaders, x.Full));   // the last 'X' wins
        Assert.Same(x, m.Find(Game with { Version = "2" }, Content, full: true));
        Assert.Equal(new CommunityEntry(Content, Obj(a), 1, 10, 2), m.Find(Game, Content));   // not asked for: the 'E'
        Assert.Equal(Obj(c), CommunityManifest.Parse(Manifest(Full(Content, c, 30), Entry(Content, a, 10))).Find(Game, Content, full: true)!.Object);

        var withdrawn = CommunityManifest.Parse(Manifest(Entry(Content, a, 10), Full(Content, c, 30), Tombstone(Content)));
        Assert.Null(withdrawn.Find(Game, Content));
        Assert.Null(withdrawn.Find(Game, Content, full: true));
        var back = CommunityManifest.Parse(Manifest(Entry(Content, a, 10), Full(Content, c, 30), Tombstone(Content), Entry(Content, d, 5)));
        Assert.Equal(new CommunityEntry(Content, Obj(d), 1, 5, 2), back.Find(Game, Content, full: true));   // a later 'E' alone

        // without an 'X', asking for it gives the 'E' exactly
        var plain = CommunityManifest.Parse(Manifest(Entry(Content, a, 10), Alias("steam:480@1", Content), Entry(Content, b, 20, 3)));
        Assert.Same(plain.Find(Game, null), plain.Find(Game, null, full: true));
        Assert.Equal(new CommunityEntry(Content, Obj(b), 1, 20, 3), plain.Find(Game, Content, full: true));
    }

    /// <summary>An index without shaders hashes to SHA-1 of nothing, every such game's: no entry, full cut, official recording
    /// or draws entry is found by it, given or through the store build's alias. A real build's hash still finds its entry.</summary>
    [Fact]
    public void Nothing_is_found_by_the_hash_of_an_index_without_shaders()
    {
        var empty = Convert.ToHexStringLower(SHA1.HashData(Array.Empty<byte>()));
        var draws = Record('D', r => Entry(Core.Planning.HashOnly.PackHash(Core.Planning.HashOnly.DrawsKey(empty)), "d"u8.ToArray(), 7).AsSpan(1).CopyTo(r[1..]));
        var m = CommunityManifest.Parse(Manifest(Entry(empty, "e"u8.ToArray(), 3), Full(empty, "x"u8.ToArray(), 4), Alias("steam:480@1", empty),
            Official(empty, "o"u8.ToArray(), 5, OfficialVisibility.Everyone), draws, Entry(Content, "c"u8.ToArray(), 6)));
        Assert.Null(m.Find(Game, empty));
        Assert.Null(m.Find(Game, empty, full: true));
        Assert.Null(m.Find(Game, null));   // the store build aliased to it
        Assert.Null(m.Find(Game, null, full: true));
        Assert.Null(m.FindOfficial(Game, empty));
        Assert.Null(m.FindOfficial(Game, null));
        Assert.Null(m.FindDraws(empty));
        Assert.Equal(6, m.Find(Game, Content)!.Psos);
    }

    /// <summary>A 'D' is a build's D3D11 draws entry, under the pack hash of its draws key: found by the build's content hash
    /// alone, apart from its 'E', and withdrawn by a tombstone of that hash.</summary>
    [Fact]
    public void Draws_entries_replay_by_the_hash_of_their_key()
    {
        var drawsHash = Core.Planning.HashOnly.PackHash(Core.Planning.HashOnly.DrawsKey(Content));
        var draws = Record('D', r => Entry(drawsHash, "d"u8.ToArray(), 7).AsSpan(1).CopyTo(r[1..]));
        var m = CommunityManifest.Parse(Manifest(Entry(Content, "e"u8.ToArray(), 3), Alias("steam:480@1", Content), draws));
        Assert.Equal((drawsHash, 7), (m.FindDraws(Content)!.ContentHash, m.FindDraws(Content)!.Psos));
        Assert.Equal(3, m.Find(Game, null)!.Psos);
        Assert.Null(m.Find(Game, drawsHash));
        Assert.Null(CommunityManifest.Parse(Manifest(draws, Tombstone(drawsHash))).FindDraws(Content));
    }

    [Fact]
    public void Official_records_replay_apart_from_the_entries_and_a_tombstone_takes_both()
    {
        var (a, b) = ("a"u8.ToArray(), "bb"u8.ToArray());
        const string other = "fedcba9876543210fedcba9876543210fedcba98";
        var m = CommunityManifest.Parse(Manifest(Alias("steam:480@1", Content), Entry(Content, a, 10), Official(Content, a, 4, OfficialVisibility.Prio),
            Official(Content, b, 5, OfficialVisibility.Everyone), Official(other, a, 1, OfficialVisibility.Patreon), Tombstone(other)));
        var o = m.FindOfficial(Game, null)!;   // by the store build
        Assert.Equal((Content, PsoDb.Hex(SHA256.HashData(b)), 2L, 5, OfficialVisibility.Everyone), (o.ContentHash, o.Object, o.Size, o.Psos, o.Visibility));   // the last 'O' wins
        Assert.Same(o, m.FindOfficial(Game with { Version = "2" }, Content));
        Assert.Equal((PsoDb.Hex(SHA256.HashData(a)), 10), (m.Find(Game, null)!.Object, m.Find(Game, null)!.Psos));   // the entry is its own
        Assert.Null(m.FindOfficial(Game, other));   // withdrawn

        var t = CommunityManifest.Parse(Manifest(Entry(Content, a, 1), Official(Content, a, 1, OfficialVisibility.Prio), Tombstone(Content)));
        Assert.Equal((null, null), (t.Find(Game, Content), t.FindOfficial(Game, Content)));
        var back = CommunityManifest.Parse(Manifest(Official(Content, a, 1, OfficialVisibility.Prio), Tombstone(Content), Entry(Content, a, 1)));
        Assert.Null(back.FindOfficial(Game, Content));   // an 'E' after the 'T' brings back the entry only

        var none = CommunityManifest.Parse(Manifest(Entry(Content, a, 3), Official(Content, a, 1, OfficialVisibility.Everyone),
            Record('O', r => Convert.FromHexString(Content).CopyTo(r[4..]))));   // an all-zero object: no official recording
        Assert.Equal((null, 3), (none.FindOfficial(Game, Content), none.Find(Game, Content)?.Psos));
        var again = CommunityManifest.Parse(Manifest(Official(Content, a, 1, OfficialVisibility.Prio), Record('O', r => Convert.FromHexString(Content).CopyTo(r[4..])),
            Official(Content, b, 2, OfficialVisibility.Patreon)));
        Assert.Equal((PsoDb.Hex(SHA256.HashData(b)), OfficialVisibility.Patreon), (again.FindOfficial(Game, Content)!.Object, again.FindOfficial(Game, Content)!.Visibility));
        Assert.Equal((OfficialVisibility)7, CommunityManifest.Parse(Manifest(Official(Content, a, 1, (OfficialVisibility)7))).FindOfficial(Game, Content)!.Visibility);
    }

    [Fact]
    public async Task An_official_recording_downloads_by_its_visibility_and_for_everyone_without_a_token()
    {
        var raw = HashOnly(new string('1', 40));
        var obj = Brotli(raw);
        var id = PsoDb.Hex(SHA256.HashData(obj));
        var fake = new Fake(_ => Ours(HttpStatusCode.OK, obj));
        var url = $"GET https://api.test.com/v1/o/{id}";

        Assert.Equal(url, await Get(OfficialVisibility.Everyone, null, null, tokenWith: false));   // signed out: no Authorization at all
        Assert.Equal(url, await Get(OfficialVisibility.Everyone, "d", "p"));   // never a token for it, signed in or not
        Assert.Equal(url + " Bearer d", await Get(OfficialVisibility.Patreon, "d", null));
        Assert.Null(await Get(OfficialVisibility.Patreon, null, "p"));   // patreon is "db"
        Assert.Equal(url + " Bearer p", await Get(OfficialVisibility.Prio, null, "p"));
        Assert.Null(await Get(OfficialVisibility.Prio, "d", null));
        Assert.Null(await Get(OfficialVisibility.Prio, "d", "p", tokenWith: false));
        Assert.Null(await Get((OfficialVisibility)7, "d", "p"));   // a rule this version doesn't know

        async Task<string?> Get(OfficialVisibility v, string? db, string? prio, bool tokenWith = true)
        {
            fake.Log.Clear();
            var community = new Community(_dir, (_, _) => Task.FromResult(db), new RouteFailover(fake, [Com, Io], _clock), _clock)
            {
                TokenWith = tokenWith ? (flag, _, _) => Task.FromResult(flag switch { "db" => db, "prio" => prio, _ => null }) : null,
            };
            var game = Path.Combine(_dir, "games", Guid.NewGuid().ToString("N"));
            var got = await community.DownloadOfficialAsync(new OfficialEntry(Content, id, obj.Length, 1, v), game);
            Assert.Equal(got != null, fake.Log.Count == 1);
            if (got != null)
            {
                Assert.Equal((id, Content, 1, v), (got.Object, got.ContentHash, got.Psos, got.Visibility));
                Assert.Equal(got, Community.DownloadedOfficial(game));
                Assert.True(PsoDb.IsCompact(Path.Combine(game, "official.db")));
                Assert.Equal(raw, RawOf(Path.Combine(game, "official.db")));
            }
            Assert.Null(Community.Downloaded(game));   // never the community recording's files
            return fake.Log.SingleOrDefault();
        }
    }

    [Fact]
    public async Task An_official_recording_for_everyone_refused_without_a_token_is_quiet_until_the_manifest_changes()
    {
        var obj = Brotli(HashOnly(new string('1', 40)));
        var file = Manifest(Official(Content, obj, 1, OfficialVisibility.Everyone));
        var fake = new Fake(r => r.RequestUri!.AbsolutePath.StartsWith("/v1/o/") ? Ours(HttpStatusCode.Forbidden) : Ours(HttpStatusCode.OK, file));
        var community = Make(fake);   // a server that gates every object on "db"
        var e = (await community.ManifestAsync())!.FindOfficial(Game, Content)!;
        var game = Path.Combine(_dir, "games", "steam_480");

        Assert.Null(await community.DownloadOfficialAsync(e, game));
        Assert.Null(community.Problem);
        Assert.Null(await community.DownloadOfficialAsync(e, game));   // the same manifest: not asked again
        Assert.Equal([$"GET https://api.test.com/v1/o/{e.Object}"], fake.Log.Where(l => l.Contains("/v1/o/")));

        file = [.. file, .. Alias("steam:480@1", Content)];
        community.Expire();
        await community.ManifestAsync();
        Assert.Null(await community.DownloadOfficialAsync(e, game));
        Assert.Equal(2, fake.Log.Count(l => l.Contains("/v1/o/")));
        Assert.Null(community.Problem);
    }

    [Fact]
    public async Task The_manifest_is_cached_for_hours_then_brought_up_to_date_with_a_range_request()
    {
        var obj = "object"u8.ToArray();
        var file = Manifest(Alias("steam:480@1", Content));
        var fake = new Fake(r =>
        {
            if (r.Headers.Range?.Ranges.Single().From is not { } from) return Ours(HttpStatusCode.OK, file);
            if (from == file.Length) return Ours(HttpStatusCode.RequestedRangeNotSatisfiable);
            var part = Ours(HttpStatusCode.PartialContent, file[(int)from..]);
            part.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, file.Length - 1, file.Length);
            return part;
        });
        var community = Make(fake);

        Assert.Null((await community.ManifestAsync())!.Find(Game, null));   // an alias with no entry points at nothing
        Assert.Equal(["GET https://api.test.com/v1/manifest/0"], fake.Log);   // no copy: epoch 0, whole

        fake.Log.Clear();
        _clock.Now += TimeSpan.FromHours(3);
        await community.ManifestAsync();
        await Make(fake).ManifestAsync();   // a restart reads the copy
        Assert.Empty(fake.Log);

        file = [.. file, .. Entry(Content, obj, 7)];   // published meanwhile
        _clock.Now += TimeSpan.FromHours(1);
        Assert.Equal(7, (await community.ManifestAsync())!.Find(Game, null)!.Psos);
        Assert.Equal(["GET https://api.test.com/v1/manifest/1 bytes=160-"], fake.Log);   // only the records after the copy's end

        fake.Log.Clear();
        _clock.Now += TimeSpan.FromHours(5);
        Assert.Equal(7, (await Make(fake).ManifestAsync())!.Find(Game, null)!.Psos);   // 416: nothing new
        Assert.Equal(["GET https://api.test.com/v1/manifest/1 bytes=240-"], fake.Log);
        Assert.Equal(file, File.ReadAllBytes(Path.Combine(_dir, "community", "manifest.bin")));
    }

    [Fact]
    public async Task A_refused_or_failed_manifest_check_waits_before_the_next()
    {
        var answer = HttpStatusCode.TooManyRequests;
        var fake = new Fake(r =>
        {
            if (answer == 0) throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable");
            var a = Ours(answer, answer == HttpStatusCode.OK ? Manifest() : null);
            if (answer == HttpStatusCode.TooManyRequests) a.Headers.RetryAfter = new(TimeSpan.FromMinutes(20));
            if (answer == HttpStatusCode.ServiceUnavailable) a.Headers.RetryAfter = new(TimeSpan.FromDays(30));
            return a;
        });
        var community = Make(fake);
        int Checks() => fake.Log.Count(l => l.Contains("/v1/manifest/"));

        Assert.Null(await community.ManifestAsync());
        community.Expire();   // a user's refresh doesn't skip the wait either
        Assert.Null(await community.ManifestAsync());
        Assert.Equal(1, Checks());
        _clock.Now += TimeSpan.FromMinutes(21);
        answer = HttpStatusCode.ServiceUnavailable;
        await community.ManifestAsync();
        Assert.Equal(2, Checks());
        _clock.Now += TimeSpan.FromHours(23);
        await community.ManifestAsync();
        Assert.Equal(2, Checks());   // 30 days asked: a day at most
        _clock.Now += TimeSpan.FromHours(1);
        answer = 0;
        await community.ManifestAsync();
        _clock.Now += TimeSpan.FromMinutes(4);
        await community.ManifestAsync();
        Assert.Equal(4, Checks());   // offline (each route once): 5 minutes
        _clock.Now += TimeSpan.FromMinutes(1);
        answer = HttpStatusCode.OK;
        Assert.NotNull(await community.ManifestAsync());
        Assert.Equal(5, Checks());
    }

    [Fact]
    public async Task Offline_or_down_keeps_the_copy_and_says_so_quietly()
    {
        var up = true;
        var fake = new Fake(r => up ? Ours(HttpStatusCode.OK, Manifest(Alias("steam:480@1", Content), Entry(Content, "o"u8.ToArray(), 3)))
            : throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable"));
        var community = Make(fake);
        await community.ManifestAsync();
        up = false;
        _clock.Now += TimeSpan.FromHours(5);
        Assert.NotNull((await community.ManifestAsync())!.Find(Game, null));
        Assert.Contains("Can't reach", community.Problem);
        var restarted = Make(new Fake(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable")));
        Assert.NotNull((await restarted.ManifestAsync())!.Find(Game, null));   // the copy on disk still answers after a restart

        var entry = (await community.ManifestAsync())!.Find(Game, null)!;
        fake.Log.Clear();
        Assert.Null(await community.DownloadAsync(entry, _dir));
        Assert.Null(await community.DownloadAsync(entry, _dir));   // offline: the next game doesn't wait for the timeouts again
        Assert.Equal(2, fake.Log.Count(l => l.Contains("/v1/o/")));   // each route once
    }

    [Fact]
    public async Task A_refused_manifest_check_waits_for_its_retry_after_instead_of_asking_once_per_game()
    {
        var refuse = true;
        var fake = new Fake(_ =>
        {
            if (!refuse) return Ours(HttpStatusCode.OK, Manifest(Alias("steam:480@1", Content)));
            var r = Ours(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new(TimeSpan.FromMinutes(30));
            return r;
        });
        var community = Make(fake);
        for (var game = 0; game < 3; game++) Assert.Null(await community.ManifestAsync());
        Assert.Single(fake.Log);
        Assert.NotNull(community.Problem);

        refuse = false;
        _clock.Now += TimeSpan.FromMinutes(31);
        Assert.NotNull(await community.ManifestAsync());
        Assert.Equal(2, fake.Log.Count);

        // Retry-After as a date
        var dated = new Fake(_ =>
        {
            var r = Ours(HttpStatusCode.ServiceUnavailable);
            r.Headers.RetryAfter = new(_clock.Now + TimeSpan.FromMinutes(10));
            return r;
        });
        var other = new Community(Path.Combine(_dir, "dated"), (_, _) => Task.FromResult<string?>("t"), new RouteFailover(dated, [Com, Io], _clock), _clock);
        await other.ManifestAsync();
        _clock.Now += TimeSpan.FromMinutes(9);
        await other.ManifestAsync();
        Assert.Single(dated.Log);
        _clock.Now += TimeSpan.FromMinutes(2);
        await other.ManifestAsync();
        Assert.Equal(2, dated.Log.Count);

        // unreachable: each route once, then not again for a while
        var down = new Fake(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable"));
        var offline = new Community(Path.Combine(_dir, "other"), (_, _) => Task.FromResult<string?>("t"), new RouteFailover(down, [Com, Io], _clock), _clock);
        for (var game = 0; game < 3; game++) Assert.Null(await offline.ManifestAsync());
        Assert.Equal(2, down.Log.Count);
        Assert.Contains("Can't reach", offline.Problem);
    }

    /// <summary>A body that sends nothing after the headers, until cancelled. <see cref="Reading"/>: its first read began.</summary>
    internal sealed class Stalled : Stream
    {
        public readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static HttpResponseMessage StalledBody(Stalled? body = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body ?? new Stalled()) };
        r.Headers.Add("X-SCSK", "1");
        return r;
    }

    [Fact]
    public async Task A_body_that_stalls_after_its_headers_is_given_up_and_backs_off()
    {
        var obj = Brotli(HashOnly(new string('1', 40)));
        var entry = new CommunityEntry(Content, PsoDb.Hex(SHA256.HashData(obj)), obj.Length, 1, 2);
        var (clock, body) = (new WelcomeTests.ManualClock(), new Stalled());
        var fake = new Fake(_ => StalledBody(body));
        var community = new Community(_dir, (_, _) => Task.FromResult<string?>("t"), new RouteFailover(fake, [Com, Io], clock), clock);
        var game = Path.Combine(_dir, "games", "steam_480");

        var download = community.DownloadAsync(entry, game);   // no CancellationToken: a background pass's
        await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(community.BodyIdle);   // the body's idle time, on the community's clock
        Assert.Null(await download.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("didn't answer in time", community.Problem);
        Assert.Null(await community.DownloadAsync(entry, game));   // backing off: not asked again yet
        Assert.Single(fake.Log);
        Assert.False(File.Exists(Path.Combine(game, "community.db")));
    }

    /// <summary>A body sent a few bytes at a time, <paramref name="gap"/> apart. Each read waits on the reading thread and
    /// completes synchronously: a Task.Delay's continuation waits for a thread-pool thread, which a loaded test run starves
    /// for seconds, so the gaps the reader saw outgrew the idle limit though the stub kept to its 250 ms.</summary>
    sealed class Dribble(byte[] data, int chunks, TimeSpan gap) : Stream
    {
        int at;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (at >= data.Length) return ValueTask.FromResult(0);
            ct.WaitHandle.WaitOne(gap);
            ct.ThrowIfCancellationRequested();   // given up while it waited
            var n = Math.Min(Math.Min(buffer.Length, (data.Length + chunks - 1) / chunks), data.Length - at);
            data.AsMemory(at, n).CopyTo(buffer);
            at += n;
            return ValueTask.FromResult(n);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_slow_body_that_keeps_coming_is_not_given_up()
    {
        var obj = Brotli(HashOnly(new string('1', 40)));
        var entry = new CommunityEntry(Content, PsoDb.Hex(SHA256.HashData(obj)), obj.Length, 1, 2);
        var fake = new Fake(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Dribble(obj, 12, TimeSpan.FromMilliseconds(250))) };
            r.Headers.Add("X-SCSK", "1");
            return r;
        });
        var community = new Community(_dir, (_, _) => Task.FromResult<string?>("t"), new RouteFailover(fake, [Com, Io], _clock), _clock)
            { BodyIdle = TimeSpan.FromSeconds(2) };
        var took = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotNull(await community.DownloadAsync(entry, Path.Combine(_dir, "games", "steam_480")));   // 12 x 250 ms: 1.5 times the idle limit in all, each gap an eighth of it
        Assert.True(took.Elapsed > community.BodyIdle);
        Assert.Null(community.Problem);
    }

    [Fact]
    public async Task A_download_is_checked_by_sha256_and_kept_with_its_pso_count()
    {
        var obj = Brotli(HashOnly(new string('1', 40)));
        var entry = new CommunityEntry(Content, PsoDb.Hex(SHA256.HashData(obj)), obj.Length, 1, 2);
        var game = Path.Combine(_dir, "games", "steam_480");
        var body = obj.ToArray();
        body[^1] ^= 1;   // corrupted on the way
        var fake = new Fake(_ => Ours(HttpStatusCode.OK, body));
        var community = Make(fake);

        Assert.Null(await community.DownloadAsync(entry, game));
        Assert.Contains("checksum", community.Problem);
        Assert.False(File.Exists(Path.Combine(game, "community.db")));
        Assert.Equal([$"GET https://api.test.com/v1/o/{entry.Object} Bearer t"], fake.Log);

        body = obj;
        var got = await community.DownloadAsync(entry, game);
        Assert.Equal((entry.Object, 1, _clock.Now), (got!.Object, got.Psos, got.DownloadedAt));
        Assert.Equal(got, Community.Downloaded(game));
        Assert.True(PsoDb.IsCompact(Path.Combine(game, "community.db")));   // kept as it came
        Assert.DoesNotContain("Full", File.ReadAllText(Path.Combine(game, "community.json")));   // an 'E' download's file as before 'X'
        Assert.Equal(HashOnly(new string('1', 40)), RawOf(Path.Combine(game, "community.db")));
        Assert.Null(community.Problem);
    }

    [Fact]
    public async Task Not_entitled_downloads_nothing()
    {
        var fake = new Fake(_ => Ours(HttpStatusCode.OK));
        Assert.Null(await Make(fake, token: null).DownloadAsync(new CommunityEntry(Content, new string('a', 64), 10, 1, 1), _dir));
        Assert.Empty(fake.Log);
    }

    [Fact]
    public async Task A_401_retries_once_with_a_fresh_token_and_a_429_backs_off()
    {
        var obj = Brotli(HashOnly(new string('1', 40)));
        var entry = new CommunityEntry(Content, PsoDb.Hex(SHA256.HashData(obj)), obj.Length, 1, 2);
        var quota = false;
        var fake = new Fake(r => quota ? WithRetryAfter(Ours(HttpStatusCode.TooManyRequests))
            : r.Headers.Authorization!.Parameter == "old" ? Ours(HttpStatusCode.Unauthorized) : Ours(HttpStatusCode.OK, obj));
        var community = Make(fake, tokens: fresh => fresh ? "new" : "old");

        Assert.NotNull(await community.DownloadAsync(entry, Path.Combine(_dir, "g1")));
        Assert.Equal([$"GET https://api.test.com/v1/o/{entry.Object} Bearer old", $"GET https://api.test.com/v1/o/{entry.Object} Bearer new"], fake.Log);

        quota = true;
        fake.Log.Clear();
        Assert.Null(await community.DownloadAsync(entry, Path.Combine(_dir, "g2")));
        Assert.Contains("limit", community.Problem);
        Assert.Null(await community.DownloadAsync(entry, Path.Combine(_dir, "g2")));   // backing off: not asked again
        Assert.Single(fake.Log, l => l.Contains("Bearer old"));
        _clock.Now += TimeSpan.FromMinutes(11);
        quota = false;
        Assert.NotNull(await community.DownloadAsync(entry, Path.Combine(_dir, "g2")));

        static HttpResponseMessage WithRetryAfter(HttpResponseMessage r) { r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10)); return r; }
    }

    /// <summary>A damaged compact recording: a record declaring 2 GB in a stream that can't seek (its Brotli) ends the read
    /// like a torn tail, with nothing allocated from that length.</summary>
    [Fact]
    public void A_record_length_past_a_streams_end_allocates_nothing()
    {
        var packed = new MemoryStream();
        using (var b = new System.IO.Compression.BrotliStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            b.Write([(byte)'S', 0xff, 0xff, 0xff, 0x7f, 1, 2, 3]);
        packed.Position = 0;
        using var s = new System.IO.Compression.BrotliStream(packed, System.IO.Compression.CompressionMode.Decompress);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Empty(PsoDb.Read(s).ToList());
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 16 << 20);
    }

    [Fact]
    public void An_entry_of_up_to_250k_records_is_read()
    {
        var rs = RootSignature();
        var sha = Sha1(rs);
        using var m = new MemoryStream();
        PsoDb.WriteBlob(m, sha, rs);   // the root signature counts too
        for (var i = 1; i < Core.Planning.HashOnly.MaxEntryRecords; i++) PsoDb.Write(m, 'C', PsoDb.Compute(sha, i.ToString("x40")));
        Assert.Equal(250_000, Core.Planning.HashOnly.MaxEntryRecords);
        Assert.Equal(249_999, Check(m.ToArray()));
        PsoDb.Write(m, 'C', PsoDb.Compute(sha, new string('f', 40)));
        Assert.Null(Check(m.ToArray()));
    }

    /// <summary>An 'X' entry holds up to 500,000 records with its 'L' records apart (at most one per PSO or state object,
    /// counted apart up to 500,000 as they decode). An 'E' keeps its 250,000 records, 'L' included.</summary>
    [Fact]
    public void An_x_entry_reads_500k_records_and_its_flags_apart()
    {
        var rs = RootSignature();
        var sha = Sha1(rs);
        using var m = new MemoryStream();
        PsoDb.WriteBlob(m, sha, rs);
        void Heads(int from, int to)
        {
            for (var i = from; i < to; i++) PsoDb.Write(m, 'C', PsoDb.Compute(sha, i.ToString("x40")));
        }
        static PsoDb.Rec Flag(string sha, int i) => new('L', Convert.FromHexString(new PsoDb.Rec('C', PsoDb.Compute(sha, i.ToString("x40"))).Key));
        int? Full(byte[] raw) => Community.CheckPacked(Brotli(raw), out _, full: true);

        Heads(1, Core.Planning.HashOnly.MaxEntryRecords);
        Assert.Equal(249_999, Full(m.ToArray()));   // a 250k 'E' as an 'X'
        Heads(Core.Planning.HashOnly.MaxEntryRecords, Core.Planning.HashOnly.MaxFullRecords);
        var heads = m.ToArray();
        Assert.Equal(500_000, Core.Planning.HashOnly.MaxFullRecords);
        Assert.Equal(499_999, Full(heads));
        Assert.Null(Check(heads));   // over the 'E' cap
        for (var i = 1; i < Core.Planning.HashOnly.MaxFullRecords; i++) PsoDb.Write(m, 'L', Flag(sha, i).Payload);
        Assert.Equal(499_999, Full(m.ToArray()));   // a million records: the 'L' don't count
        PsoDb.Write(m, 'L', Flag(sha, 1).Payload);
        Assert.Null(Full(m.ToArray()));   // more 'L' than heads
        Assert.Null(Full([.. heads, .. Rec('C', PsoDb.Compute(sha, new string('f', 40)))]));   // 500,001 records

        // the 'L' count stops the read at the 500,001st, whatever follows
        var flags = Enumerable.Range(1, Core.Planning.HashOnly.MaxFullRecords + 1).Select(i => Flag(sha, i));
        var e = Assert.Throws<InvalidDataException>(() => Core.Planning.HashOnly.Verify(flags, Core.Planning.HashOnly.MaxEntryStateObjectRefs,
            Core.Planning.HashOnly.MaxFullRecords, flagsApart: true));
        Assert.Equal("more than 500000 'L' records", e.Message);

        // an 'E' counts its 'L': 249,998 heads and 2 'L' are 250,001 records with the root signature
        using var few = new MemoryStream();
        PsoDb.WriteBlob(few, sha, rs);
        for (var i = 1; i < Core.Planning.HashOnly.MaxEntryRecords - 1; i++) PsoDb.Write(few, 'C', PsoDb.Compute(sha, i.ToString("x40")));
        PsoDb.Write(few, 'L', Flag(sha, 1).Payload);
        Assert.Equal(249_998, Check(few.ToArray()));
        PsoDb.Write(few, 'L', Flag(sha, 2).Payload);
        Assert.Null(Check(few.ToArray()));
        Assert.Equal(249_998, Full(few.ToArray()));
    }

    /// <summary>A full cut the server wrote (server-rs content::cap::full, every_full_cut_is_closed) and its manifest: the
    /// 'X' is found on NVIDIA's lookup only, and its object reads as an 'X' and as any recording.</summary>
    [Fact]
    public void A_server_full_cut_reads_as_an_x_entry()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "SCSKiller.slnx"))) root = root.Parent!;
        var dir = Path.Combine(root.FullName, "server-rs", "conformance", "fixtures", "full");
        if (!Directory.Exists(dir)) return;
        var packed = File.ReadAllBytes(Path.Combine(dir, "x.br"));
        var m = CommunityManifest.Parse(File.ReadAllBytes(Path.Combine(dir, "x.manifest")));
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var x = m.Find(Game, hash, full: true)!;
        var e = m.Find(Game, hash)!;
        Assert.True(x.Full && !e.Full && x.Object != e.Object);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(packed)), x.Object);
        Assert.Equal(packed.Length, x.Size);
        var psos = Community.CheckPacked(packed, out _, full: true);
        Assert.Equal(x.Psos, psos);
        Assert.Equal(psos, Check(Core.Planning.HashOnly.Decompress(packed).SelectMany(r => Rec(r.Tag, r.Payload)).ToArray()));
    }

    static byte[] Rec(char tag, byte[] payload)
    {
        using var m = new MemoryStream();
        PsoDb.Write(m, tag, payload);
        return m.ToArray();
    }

    [Fact]
    public async Task A_download_over_the_decompressed_cap_is_ignored()
    {
        Assert.Equal(320 << 20, Community.MaxRaw);   // a valid download between 256 and 320 MiB would hold about 1 GB in the test
        var rs = RootSignature(Core.Planning.HashOnly.MaxRootSignature);
        var sha = Sha1(rs);
        void Write(Stream s, long copies)
        {
            for (var i = 0L; i < copies; i++) PsoDb.WriteBlob(s, sha, rs);   // the same root signature again: allowed, kept once
            PsoDb.Write(s, 'C', PsoDb.Compute(sha, new string('1', 40)));
        }
        using (var few = new MemoryStream())
        {
            Write(few, 3);
            Assert.Equal(1, Check(few.ToArray()));
        }
        using var packed = new MemoryStream();
        using (var b = new BrotliStream(packed, CompressionLevel.Fastest, leaveOpen: true)) Write(b, Community.MaxRaw / (25L + rs.Length) + 1);
        var obj = packed.ToArray();
        var entry = new CommunityEntry(Content, PsoDb.Hex(SHA256.HashData(obj)), obj.Length, 1, 2);
        var game = Path.Combine(_dir, "games", "steam_480");
        var community = Make(new Fake(_ => Ours(HttpStatusCode.OK, obj)));

        Assert.Null(await community.DownloadAsync(entry, game));
        Assert.Contains("can't read was ignored", community.Problem);
        Assert.False(File.Exists(Path.Combine(game, "community.db")));
        Assert.Equal(1, (await community.DownloadAsync(entry with { Full = true }, game))?.Psos);   // within an 'X' entry's cap
        Assert.True(Community.Downloaded(game)!.Full);
    }

    /// <summary>A community.db that can't be replaced leaves no community.json beside it: neither download is used.</summary>
    [Fact]
    public async Task A_community_recording_that_cant_be_replaced_is_no_longer_named()
    {
        var obj = Brotli(HashOnly(new string('1', 40)));
        var entry = new CommunityEntry(Content, PsoDb.Hex(SHA256.HashData(obj)), obj.Length, 1, 2);
        var game = Path.Combine(_dir, "games", "steam_480");
        var community = Make(new Fake(_ => Ours(HttpStatusCode.OK, obj)));
        Assert.NotNull(await community.DownloadAsync(entry, game));

        File.Delete(Path.Combine(game, "community.db"));
        Directory.CreateDirectory(Path.Combine(game, "community.db"));   // the write fails
        Assert.Null(await community.DownloadAsync(entry with { Full = true }, game));
        Assert.Null(Community.Downloaded(game));
        Assert.False(File.Exists(Path.Combine(game, "community.json")));
    }

    [Fact]
    public async Task An_x_download_over_its_caps_is_ignored()
    {
        Assert.Equal(768 << 20, Community.MaxFullRaw);
        var rs = RootSignature(Core.Planning.HashOnly.MaxRootSignature);
        var sha = Sha1(rs);
        using var packed = new MemoryStream();
        using (var b = new BrotliStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var i = 0L; i < Community.MaxFullRaw / (25L + rs.Length) + 1; i++) PsoDb.WriteBlob(b, sha, rs);
            PsoDb.Write(b, 'C', PsoDb.Compute(sha, new string('1', 40)));
        }
        var obj = packed.ToArray();
        var entry = new CommunityEntry(Content, PsoDb.Hex(SHA256.HashData(obj)), obj.Length, 1, 2, Full: true);
        var game = Path.Combine(_dir, "games", "steam_480");
        var fake = new Fake(_ => Ours(HttpStatusCode.OK, obj));
        var community = Make(fake);

        Assert.Null(await community.DownloadAsync(entry, game));
        Assert.Contains("can't read was ignored", community.Problem);
        Assert.False(File.Exists(Path.Combine(game, "community.db")));

        fake.Log.Clear();   // a size over the cap: not asked for
        Assert.Null(await community.DownloadAsync(entry with { Size = Community.MaxFullRaw + 1L }, game));
        Assert.Null(await community.DownloadAsync(entry with { Size = Community.MaxRaw + 1L, Full = false }, game));
        Assert.Empty(fake.Log);
    }

    [Fact]
    public void Only_root_signatures_and_pipelines_are_accepted()
    {
        Assert.Equal(1, Check(HashOnly(new string('1', 40))));
        var shader = "DXBC shader bytes"u8.ToArray();
        using var m = new MemoryStream();
        PsoDb.WriteBlob(m, Sha1(shader), shader);   // shader code: never from the database
        Assert.Null(Check(m.ToArray()));
        var rs = RootSignature();
        m.SetLength(0);
        PsoDb.WriteBlob(m, new string('0', 40), rs);   // a root signature under another hash
        Assert.Null(Check(m.ToArray()));
        m.SetLength(0);
        PsoDb.Write(m, 'P', new byte[48]);   // a plan item
        Assert.Null(Check(m.ToArray()));
        Assert.Null(Check([.. HashOnly(new string('1', 40)), (byte)'S', 9, 0]));   // a torn tail
        // a root-signature-only container whose RTS0 part is empty: nothing a planner could read
        byte[] empty = [.. "DXBC"u8, .. new byte[16], 1, 0, 0, 0, 44, 0, 0, 0, 1, 0, 0, 0, 36, 0, 0, 0, .. "RTS0"u8, 0, 0, 0, 0];
        m.SetLength(0);
        PsoDb.WriteBlob(m, Sha1(empty), empty);
        PsoDb.Write(m, 'C', PsoDb.Compute(Sha1(empty), Sha1(SharingTests.Shader)));
        Assert.Null(Check(m.ToArray()));
    }

    /// <summary>A local recording with one record of every tag the recorder writes (proxy.cpp: 'B' root signature and shader
    /// blobs, 'G' / 'C' / 'S' PSOs, 'R' / 'A' ray tracing state objects, 'N' NVAPI state), and the 'L' flag an upload adds
    /// (<see cref="Core.Planning.HashOnly.LocalOnly"/>): stripped to hash-only (<see cref="Core.Planning.HashOnly.Canonical"/>,
    /// what Sharing and admin import do), compressed and back, checked like a download (<see cref="Community.Check"/>) and by
    /// the server's rule, every tag but the shader blobs is still there; the DXIL library is a hash the install rehydrates.</summary>
    internal static List<PsoDb.Rec> EveryRecorderTag(out string libSha)
    {
        var rs = RootSignature();
        var lib = Planning.RtCollectionTests.Library((10, "MaterialCHS", 64));
        libSha = Sha1(lib);
        var collection = new PsoDb.Rec('R', RtCollections.Collection(lib, libSha, Sha1(rs), Sha1(rs), Sha1(rs), 64, 8, 1, 4)!);
        var compute = new PsoDb.Rec('C', PsoDb.Compute(Sha1(rs), Sha1(SharingTests.Shader)));
        return [new('B', [.. Convert.FromHexString(Sha1(rs)), .. rs]), new('B', [.. Convert.FromHexString(libSha), .. lib]),
            new('B', [.. Convert.FromHexString(Sha1(SharingTests.Shader)), .. SharingTests.Shader]),
            new('G', new byte[616]), compute,
            new('S', PsoDb.Stream(Sha1(rs), new Dictionary<int, string> { [(int)Stage.Vertex] = Sha1(SharingTests.Shader) }, [], 3, [PsoDb.R16G16B16A16Float], 0)),
            collection, new('A', [.. Convert.FromHexString(collection.Key), .. collection.Payload]),
            new PsoDb.NvState(collection.Key, 0, 1001, 2, 0).ToRec(), new PsoDb.NvState(compute.Key, 0, 1001, 3, 0).ToRec(),
            new('L', Convert.FromHexString(compute.Key))];
    }

    [Fact]
    public void Every_recorder_tag_survives_the_hash_only_round_trip()
    {
        var local = EveryRecorderTag(out var libSha);
        Assert.Equal(Core.Planning.HashOnly.Tags.Replace("D", "").Order(), local.Select(r => r.Tag).Distinct().Order());   // 'D' travels in its own entry
        var raw = new MemoryStream();
        var canonical = Core.Planning.HashOnly.Canonical(local, local: true, out var dropped);
        foreach (var r in canonical) PsoDb.Write(raw, r.Tag, r.Payload);
        Assert.Equal((2, 0), (dropped.ShaderBlobs, dropped.StateObjects)); // the shader and the library
        var back = Core.Planning.HashOnly.Decompress(Core.Planning.HashOnly.Compress(PsoDb.Read(new MemoryStream(raw.ToArray()))));
        Assert.Equal(3, Check(raw.ToArray()));
        Assert.Equal(back, Core.Planning.HashOnly.Canonical(back, local: false, out _));
        Assert.Equal(Core.Planning.HashOnly.Tags.Replace("D", "").Order(), back.Select(r => r.Tag).Distinct().Order());
        Assert.Equal(local.Where(r => r.Tag != 'B').Select(r => r.Key).Order(), back.Where(r => r.Tag != 'B').Select(r => r.Key).Order());
        Assert.DoesNotContain(back, r => r.Tag == 'B' && PsoDb.Hex(r.Payload.AsSpan(0, 20)) == libSha);
        Assert.Contains(libSha, Rehydrate.References(back)); // pulled from the install like any shader
    }

    /// <summary>A download checked as it decodes (<see cref="Community.CheckPacked"/>) is refused or counted as the same
    /// bytes checked whole (<see cref="Community.Check"/>): an entry of every tag, each of its truncations and each
    /// single-byte change, and what Canonical lets through or refuses past the records themselves.</summary>
    [Fact]
    public void A_download_checked_as_it_decodes_is_judged_as_a_whole_one()
    {
        var canonical = Core.Planning.HashOnly.Canonical(EveryRecorderTag(out _), local: true, out _);
        byte[] Db(IEnumerable<PsoDb.Rec> records)
        {
            using var m = new MemoryStream();
            foreach (var r in records) PsoDb.Write(m, r.Tag, r.Payload);
            return m.ToArray();
        }
        var raw = Db(canonical);
        Assert.Equal(3, Check(raw));
        for (var n = 0; n < raw.Length; n++) Check(raw[..n]);
        for (var i = 0; i < raw.Length; i++)
        {
            var changed = raw.ToArray();
            changed[i] ^= 0xFF;
            Check(changed);
        }
        var addition = canonical.Single(r => r.Tag == 'A');
        PsoDb.Rec[] more =
        [
            new('L', Enumerable.Repeat((byte)0x11, 20).ToArray()),   // a flag about a record it lacks: dropped, not refused
            new PsoDb.NvState(new string('2', 40), 0, 1, 2, 0).ToRec(),
            canonical.First(r => r.Tag == 'S'),   // twice
            addition,
            new('W', new byte[40]),
            new('D', PsoDb.Draw11Bytes(new(new string('a', 40), PsoDb.Zero, PsoDb.Zero, PsoDb.Zero, 4, true, []))),
            new('P', new byte[48]),
            new('A', [.. Enumerable.Repeat((byte)0x22, 20), .. addition.Payload[20..]]),   // built on a record it lacks
        ];
        Assert.Equal([3, 3, 3, 3, null, null, null, null], more.Select(r => Check(Db([.. canonical, r]))));
        Assert.Null(Check(Db(canonical.Where(r => r.Tag != 'B'))));   // state objects without their root signature
        Assert.Null(Check(Db(canonical.Where(r => r.Tag != 'R'))));   // an addition without its base
        Assert.Null(Check(Db(canonical.Where(r => r.Tag is 'B' or 'N' or 'L'))));   // nothing to plan
        Assert.Equal(3, Check(Db(canonical.Where(r => r.Tag is not ('R' or 'A')))));
    }

    [Fact]
    public void The_union_adds_each_downloads_records_the_ones_before_lack()
    {
        Directory.CreateDirectory(_dir);
        string community = Path.Combine(_dir, "community.db"), official = Path.Combine(_dir, "official.db"), all = Path.Combine(_dir, "merged.db");
        File.WriteAllBytes(community, [.. HashOnly(new string('1', 40)), .. HashOnly(new string('2', 40))]);
        File.WriteAllBytes(official, [.. HashOnly(new string('2', 40)), .. HashOnly(new string('3', 40))]);
        Community.Union(Path.Combine(_dir, "none.db"), [community, official], all);
        var recs = PsoDb.Read(all).ToList();
        Assert.Equal(PsoDb.Read(community).Concat(PsoDb.Read(official)).Select(r => r.Key).Distinct().Order(), recs.Select(r => r.Key).Order());
        Assert.Equal(4, recs.Count);   // one root signature, three pipelines
    }

    [Fact]
    public void The_union_keeps_local_records_and_adds_the_communitys_blobs_first()
    {
        Directory.CreateDirectory(_dir);
        string local = Path.Combine(_dir, "recording.db"), community = Path.Combine(_dir, "community.db"), all = Path.Combine(_dir, "merged.db");
        var vs = "local vs"u8.ToArray();
        PsoDb.WriteCompact(local, [new('B', [.. Convert.FromHexString(Sha1(vs)), .. vs]),
            new('S', PsoDb.Stream(PsoDb.Zero, new Dictionary<int, string> { [(int)Stage.Vertex] = Sha1(vs) }, [], 3, [], 0))]);
        File.WriteAllBytes(community, [.. HashOnly(Sha1(vs)), .. HashOnly(new string('2', 40))]);

        Community.Union(local, [community], all);
        var recs = PsoDb.Read(all).ToList();
        Assert.False(PsoDb.IsCompact(all));   // a proxy db, for the planner and the warm
        Assert.Equal(PsoDb.Read(local).Concat(PsoDb.Read(community)).Select(r => r.Key).Distinct().Order(), recs.Select(r => r.Key).Order());
        Assert.All(recs.SkipWhile(r => r.Tag == 'B'), r => Assert.NotEqual('B', r.Tag));
        Assert.Equal(vs, recs[0].Payload[20..]);   // local first

        File.Delete(local);
        Community.Union(local, [community], all);
        Assert.Equal(PsoDb.Read(community).Select(r => r.Key).Distinct().Order(), PsoDb.Read(all).Select(r => r.Key).Order());
    }
}
