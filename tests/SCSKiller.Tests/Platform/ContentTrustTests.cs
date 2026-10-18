using System.Text.Json.Nodes;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// Signed content files (Core/App/ContentTrust.cs). The keys here are generated per run: a test key never exists outside
// this file, and production pins only ContentTrust.RulesKeys.
public class ContentTrustTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-content-test-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly (string Seed, string Public) A = FeedTrust.NewKey(), B = FeedTrust.NewKey();
    const string Body = """{"rules":[{"id":"red3","requires":["rs.desc.v1"]}]}""";
    static readonly DateTimeOffset T = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    ContentTrust Trust() => new(new AppStore(_dir), new Dictionary<string, string> { ["rules-a"] = A.Public, ["rules-b"] = B.Public });
    static string Env(string seed, string kid, string name, string body, DateTimeOffset at) => ContentTrust.Sign(Convert.FromBase64String(seed), kid, name, body, at);
    static string? Json(string s) => s.StartsWith('{') ? s : null;

    static void Refused(ContentTrust t, string envelope, string why, string name = "plan-rules")
    {
        Assert.Null(t.Accept(name, envelope, Json));
        Assert.Contains(why, t.Rejected);
    }

    [Fact]
    public void GoodEnvelope_WithEitherPinnedKey_GivesItsBody()
    {
        var t = Trust();
        Assert.Equal(Body, t.Accept("plan-rules", Env(A.Seed, "rules-a", "plan-rules", Body, T), Json));
        Assert.Null(t.Rejected);
        Assert.Equal(Body, t.Accept("plan-rules", Env(B.Seed, "rules-b", "plan-rules", Body, T.AddHours(1)), Json));   // the backup key
        Assert.Equal(Body, t.Accept("plan-rules", Env(B.Seed, "rules-b", "plan-rules", Body, T.AddHours(1)), Json));   // the cached copy at the next start
    }

    [Fact]
    public void BadSignatures_AreRefused()
    {
        var t = Trust();
        var other = FeedTrust.NewKey();
        Refused(t, Env(other.Seed, "rules-a", "plan-rules", Body, T), "bad signature");          // not the pinned key
        Refused(t, Env(other.Seed, "rules-z", "plan-rules", Body, T), "unknown key");            // an unpinned kid
        Refused(t, Env(A.Seed, "rules-a", "known-stutter", Body, T), "signed for 'known-stutter'");   // another file's envelope served under this name
        Refused(t, Body, "malformed");                                                           // an unsigned body
        Refused(t, "not json", "malformed");
        Refused(new ContentTrust(new AppStore(_dir), ContentTrust.RulesKeys), Env(A.Seed, "rules-a", "plan-rules", Body, T), "bad signature");   // the pinned keys refuse a test key
        Refused(new ContentTrust(new AppStore(_dir), ContentTrust.RulesKeys), Env(A.Seed, "rules-b", "plan-rules", Body, T), "bad signature");
    }

    [Fact]
    public void ReleaseFeedSignature_DoesntSignContent()
    {
        var t = Trust();
        var feedSig = JsonNode.Parse(FeedTrust.Sign(Convert.FromBase64String(A.Seed), "rules-a", "plan-rules", System.Text.Encoding.UTF8.GetBytes(Body), T))!;
        var j = JsonNode.Parse(Env(A.Seed, "rules-a", "plan-rules", Body, T))!;
        j["sig"] = feedSig["sig"]!.GetValue<string>();   // the same key and fields under the feed's message prefix
        Refused(t, j.ToJsonString(), "bad signature");
    }

    [Fact]
    public void TamperedEnvelope_IsRefused()
    {
        var t = Trust();
        var good = Env(A.Seed, "rules-a", "plan-rules", Body, T);
        foreach (var (field, value, why) in new[]
        {
            ("body", Body.Replace("red3", "red4"), "doesn't match"),
            ("signed_at", "2027-01-01T00:00:00Z", "bad signature"),
            ("sha256", new string('0', 64), "bad signature"),
            ("sig", "not base64!", "malformed signature"),
        })
        {
            var j = JsonNode.Parse(good)!;
            j[field] = value;
            Refused(t, j.ToJsonString(), why);
        }
        var extra = JsonNode.Parse(good)!;
        extra["note"] = "x";
        Refused(t, extra.ToJsonString(), "malformed");
        var v2 = JsonNode.Parse(good)!;
        v2["v"] = 2;
        Refused(t, v2.ToJsonString(), "version");
    }

    [Fact]
    public void OlderEnvelope_IsARefusedReplay_PerName()
    {
        var t = Trust();
        Assert.NotNull(t.Accept("plan-rules", Env(A.Seed, "rules-a", "plan-rules", Body, T), Json));
        Refused(t, Env(A.Seed, "rules-a", "plan-rules", Body, T.AddMinutes(-1)), "older");
        Assert.NotNull(t.Accept("other-rules", Env(A.Seed, "rules-a", "other-rules", Body, T.AddDays(-30)), Json));   // another name keeps its own time
        Refused(Trust(), Env(A.Seed, "rules-a", "plan-rules", Body, T.AddMinutes(-1)), "older");   // remembered on disk
    }

    [Fact]
    public void UnusableBody_DoesntLockOutTheLastGoodOne()
    {
        var t = Trust();
        Assert.NotNull(t.Accept("plan-rules", Env(A.Seed, "rules-a", "plan-rules", Body, T), Json));
        Refused(t, Env(A.Seed, "rules-a", "plan-rules", "[]", T.AddHours(1)), "body isn't valid");   // signed, newer, but the app can't use it
        Assert.NotNull(t.Accept("plan-rules", Env(A.Seed, "rules-a", "plan-rules", Body, T), Json));   // the cached good one still loads
    }

    // a body of a three-digit number is that status
    sealed class Edge(Dictionary<string, string> files, List<string>? asked = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath.TrimStart('/');
            lock (files) asked?.Add(path);
            return Task.FromResult(!files.TryGetValue(path, out var body) ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : int.TryParse(body, out var status) ? new HttpResponseMessage((System.Net.HttpStatusCode)status)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Fetch_TakesItsChannelsCopyEvenWhenOlder_AndStablesWhenTheChannelHasNoneOrItDoesntVerify()
    {
        Directory.CreateDirectory(_dir);
        var cache = Path.Combine(_dir, "game-verdicts.json");
        var asked = new List<string>();
        Task<string?> Fetch(Dictionary<string, string> files, params string[] channels) =>
            Trust().FetchAsync("game-verdicts", Json, cache, channels, "tok", new RouteFailover(new Edge(files, asked), [new("https://api.scskiller.com/")]));
        Dictionary<string, string> files = new()
        {
            ["v1/content/game-verdicts.json"] = Env(A.Seed, "rules-a", "game-verdicts", """{"games":[1]}""", T.AddHours(1)),
            ["v1/updates/beta/game-verdicts.json"] = Env(A.Seed, "rules-a", "game-verdicts", """{"games":[2]}""", T),
        };
        Assert.Equal("""{"games":[2]}""", await Fetch(files, "beta"));
        Assert.Equal("""{"games":[2]}""", Trust().Cached("game-verdicts", Json, cache, null));   // a start before the account answers

        files["v1/updates/beta/game-verdicts.json"] = "403";
        Assert.Equal("""{"games":[2]}""", await Fetch(files, "beta"));   // a refusal keeps the channel's last copy
        files["v1/updates/beta/game-verdicts.json"] = Env(FeedTrust.NewKey().Seed, "rules-a", "game-verdicts", """{"games":[3]}""", T.AddHours(2));
        Assert.Equal("""{"games":[2]}""", await Fetch(files, "beta"));   // so does a copy that doesn't verify
        files.Remove("v1/updates/beta/game-verdicts.json");
        Assert.Equal("""{"games":[1]}""", await Fetch(files, "beta"));
        Assert.False(File.Exists(ContentTrust.CacheFile(Path.Combine(_dir, "game-verdicts.json"), "beta")));

        files["v1/updates/beta/game-verdicts.json"] = Env(FeedTrust.NewKey().Seed, "rules-a", "game-verdicts", """{"games":[3]}""", T.AddHours(2));
        Assert.Equal("""{"games":[1]}""", await Fetch(files, "beta"));

        files["v1/updates/beta/game-verdicts.json"] = Env(A.Seed, "rules-a", "game-verdicts", """{"games":[2]}""", T.AddHours(2));
        asked.Clear();
        Assert.Equal("""{"games":[1]}""", await Fetch(files));   // stable
        Assert.Equal(["v1/content/game-verdicts.json"], asked);
    }

    [Fact]
    public async Task Fetch_TakesTheNewestCopyOfItsOwnNameAmongTheChannels()
    {
        Directory.CreateDirectory(_dir);
        var cache = Path.Combine(_dir, "news.json");
        Task<string?> Fetch(Dictionary<string, string> files, params string[] channels) =>
            Trust().FetchAsync("news", Json, cache, channels, "tok", new RouteFailover(new Edge(files), [new("https://api.scskiller.com/")]));
        var stable = Env(A.Seed, "rules-a", "news", """{"items":[1]}""", T);
        Dictionary<string, string> files = new()
        {
            ["v1/content/news.json"] = stable,
            ["v1/updates/beta/news.json"] = Env(A.Seed, "rules-a", "plan-rules", Body, T.AddHours(1)),   // newer, but another file's
        };
        Assert.Equal("""{"items":[1]}""", await Fetch(files, "beta"));
        Assert.Equal(stable, File.ReadAllText(cache));
        files["v1/updates/beta/news.json"] = Env(B.Seed, "rules-b", "news", """{"items":[2]}""", T.AddHours(1));
        Assert.Equal("""{"items":[2]}""", await Fetch(files, "beta"));
        Assert.Equal("""{"items":[1]}""", await Fetch(files));   // beta no longer read: stable's older copy, its own channel's newest
        Assert.False(File.Exists(Path.Combine(_dir, "news@beta.json")));
        files["v1/content/news.json"] = Env(A.Seed, "rules-a", "news", """{"items":[3]}""", T.AddHours(2));
        Assert.Equal("""{"items":[3]}""", await Fetch(files, "beta"));   // unlike game verdicts, a newer stable copy wins over the channel's
    }

    [Fact]
    public void Envelope_OverTheContentCap_IsRefused() =>
        Refused(Trust(), Env(A.Seed, "rules-a", "plan-rules", "{\"x\":\"" + new string('a', ContentFile.MaxBytes) + "\"}", T), "malformed");
}
