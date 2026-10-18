using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// news.json (Core/App/News.cs) and What's new (Core/App/WhatsNew.cs).
public class NewsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-news-test-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly (string Seed, string Public) A = FeedTrust.NewKey();
    static readonly DateTimeOffset T = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    static readonly DateOnly Today = new(2026, 10, 4);

    public NewsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    static string Doc(params string[] items) => $$"""{"items":[{{string.Join(",", items)}}]}""";
    static string Item(string id, string more = "") => $$"""{"id":"{{id}}","date":"2026-10-01","title":"T","body":"B"{{more}}}""";
    static NewsTarget Target(string version = "1.3.0", string[]? ent = null, GpuVendor gpu = GpuVendor.Nvidia, params string[] games) =>
        new(AppVersion.Parse(version)!, ent ?? [], gpu, games.Contains);
    static string[] Ids(News n, NewsTarget t, DateOnly? today = null) => n.For(t, today ?? Today).Select(i => i.Id).ToArray();

    [Fact]
    public void Targeting_IsFilteredOnThePc()
    {
        var n = News.TryParse(Doc(
            Item("all"),
            Item("beta", ""","channels":["beta"]"""),
            Item("supporters", ""","ent":["db"]"""),
            Item("free", ""","ent":["none"]"""),
            Item("old-app", ""","maxVersion":"1.2.9" """),
            Item("range", ""","minVersion":"1.3.0-beta.1","maxVersion":"1.3.0" """),
            Item("amd", ""","gpu":["amd"]"""),
            Item("elden", ""","game":"steam:1245620" """)))!;
        Assert.Equal(["all", "elden", "free", "range"], Ids(n, Target(games: "steam:1245620")).Order());
        Assert.Equal(["all", "beta", "range", "supporters"], Ids(n, Target("1.3.0-beta.2", ["db", "beta"])).Order());
        Assert.Equal(["all", "amd", "free", "old-app"], Ids(n, Target("1.2.3", gpu: GpuVendor.Amd)).Order());
    }

    [Fact]
    public void ExpiredItems_AreHidden_NewestFirst()
    {
        var n = News.TryParse(Doc(Item("a", ""","expires":"2026-10-04" """), Item("b", ""","expires":"2026-10-05","date":"2026-10-02" """).Replace("\"date\":\"2026-10-01\",", "")))!;
        Assert.Equal(["b"], Ids(n, Target()));
        Assert.Equal(["b", "a"], Ids(n, Target(), new(2026, 10, 3)));
        Assert.Empty(Ids(n, Target(), new(2026, 10, 5)));
    }

    [Fact]
    public void LinkAllowlist()
    {
        foreach (var ok in new[] { "https://scskiller.com/blog", "https://www.patreon.com/SCSKiller", "https://github.com/BlueHeisenberg/SCSKiller/releases",
                     "https://github.com/BlueHeisenberg", "https://discord.gg/abc", "https://discord.com/invite/abc" })
            Assert.True(News.IsAllowedLink(ok), ok);
        foreach (var bad in new[] { "http://scskiller.com/", "https://github.com/someone/SCSKiller", "https://github.com/BlueHeisenbergX/a",
                     "https://scskiller.com.evil.example/", "https://evil.example/?https://scskiller.com", "https://discord.gg:8443/abc",
                     "https://user@discord.com/", "javascript:alert(1)", "scskiller.com" })
            Assert.False(News.IsAllowedLink(bad), bad);
    }

    [Fact]
    public void AnInvalidItem_IsDroppedAlone()
    {
        var n = News.TryParse(Doc(Item("good"), Item("bad-link", ""","link":"https://evil.example/" """), Item("newer-field", ""","lang":["de"]"""),
            Item("Bad Id"), Item("bad-date").Replace("2026-10-01", "1 Oct"), Item("good")))!;   // a second "good" is a duplicate
        Assert.Equal(["good"], n.Items.Select(i => i.Id));
        Assert.Null(News.TryParse("""{"items":[],"extra":1}"""));
    }

    ContentTrust Trust() => new(new AppStore(_dir), new Dictionary<string, string> { ["rules-a"] = A.Public });
    static string Sign(string body, string name = News.Name, string? seed = null) =>
        ContentTrust.Sign(Convert.FromBase64String(seed ?? A.Seed), "rules-a", name, body, T);

    [Fact]
    public void OnlySignedNewsIsUsed()
    {
        var file = Path.Combine(_dir, "news.json");
        var body = Doc(Item("hello"));
        foreach (var refused in new[] { body, Sign(body, seed: FeedTrust.NewKey().Seed), Sign(body, name: "plan-rules"), Sign("""{"items":"x"}""") })
        {
            File.WriteAllText(file, refused);
            Assert.Null(News.Cached(file, Trust(), []));
        }
        File.WriteAllText(file, Sign(body));
        Assert.Null(News.Cached(file, new ContentTrust(new AppStore(_dir), ContentTrust.RulesKeys), []));   // nothing pinned
        Assert.Equal(["hello"], News.Cached(file, Trust(), [])!.Items.Select(i => i.Id));
    }

    sealed class Edge(string? body) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Asked.Add(r.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(body == null ? System.Net.HttpStatusCode.NotFound : System.Net.HttpStatusCode.OK) { Content = new StringContent(body ?? "{}") });
        }
    }

    [Fact]
    public async Task Fetch_KeepsOnlyAValidSignedCopy()
    {
        var file = Path.Combine(_dir, "news.json");
        var unsigned = new Edge(Doc(Item("x")));
        Assert.Null(await News.FetchAsync(file, Trust(), new RouteFailover(unsigned, [new("https://api.scskiller.com/")])));
        Assert.Equal(["/" + News.ContentPath], unsigned.Asked);
        Assert.False(File.Exists(file));
        var signed = Sign(Doc(Item("x")));
        Assert.Equal(["x"], (await News.FetchAsync(file, Trust(), new RouteFailover(new Edge(signed), [new("https://api.scskiller.com/")])))!.Items.Select(i => i.Id));
        Assert.Equal(signed, File.ReadAllText(file));
    }

    /// <summary>A path missing from <paramref name="files"/> is 404; a channel's without the token "tok" is 401.</summary>
    sealed class ChannelEdge(Dictionary<string, string> files) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath.TrimStart('/');
            lock (Asked) Asked.Add(path + (r.Headers.Authorization is { } a ? " " + a.Parameter : ""));
            var status = path.StartsWith("v1/updates/") && r.Headers.Authorization?.Parameter != "tok" ? System.Net.HttpStatusCode.Unauthorized
                : files.TryGetValue(path, out _) ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.NotFound;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(files.GetValueOrDefault(path, "{}")) });
        }
    }

    [Fact]
    public async Task TheUpdateChannelSaysWhichFileIsRead()
    {
        Assert.Empty(News.Channels(UpdateChannels.Stable));
        Assert.Equal(["beta"], News.Channels(UpdateChannels.Beta));
        Assert.Equal(["alpha"], News.Channels(UpdateChannels.Alpha));
        Assert.Equal(["alpha"], News.Channels(UpdateChannels.Internal));

        const string Stable = "v1/content/news.json", Beta = "v1/updates/beta/news.json", Alpha = "v1/updates/alpha/news.json";
        string At(string id, int hours) => ContentTrust.Sign(Convert.FromBase64String(A.Seed), "rules-a", News.Name, Doc(Item(id)), T.AddHours(hours));
        Dictionary<string, string> files = new() { [Stable] = At("stable", 0), [Beta] = At("beta", 1), [Alpha] = At("alpha", 2) };
        async Task<(string? Id, ChannelEdge Edge)> Fetch(string? token, string channel)
        {
            var edge = new ChannelEdge(files);
            var n = await News.FetchAsync(Path.Combine(_dir, "news.json"), Trust(), new RouteFailover(edge, [new("https://api.scskiller.com/")]), token, News.Channels(channel));
            return (n?.Items.Single().Id, edge);
        }

        var (id, edge) = await Fetch("tok", UpdateChannels.Stable);
        Assert.Equal("stable", id);
        Assert.Equal([Stable], edge.Asked);
        (id, edge) = await Fetch("tok", UpdateChannels.Beta);
        Assert.Equal("beta", id);
        Assert.Equal([Stable, Beta + " tok"], edge.Asked.Order());
        (id, _) = await Fetch("tok", UpdateChannels.Internal);
        Assert.Equal("alpha", id);
        Assert.Equal(files[Alpha], File.ReadAllText(Path.Combine(_dir, "news@alpha.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "news@beta.json")));   // beta's isn't read on alpha

        Directory.Delete(_dir, true);
        Directory.CreateDirectory(_dir);
        (id, edge) = await Fetch(null, UpdateChannels.Alpha);   // signed out: stable only, no token sent
        Assert.Equal("stable", id);
        Assert.Equal([Stable], edge.Asked);
        files[Stable] = At("everyone", 3);   // a stable file newer than the channel's reaches that channel too
        Assert.Equal("everyone", (await Fetch("tok", UpdateChannels.Alpha)).Id);
    }

    [Fact]
    public async Task LeavingAChannelGoesBackToStablesNews()
    {
        var file = Path.Combine(_dir, "news.json");
        string At(string id, int hours) => ContentTrust.Sign(Convert.FromBase64String(A.Seed), "rules-a", News.Name, Doc(Item(id)), T.AddHours(hours));
        Dictionary<string, string> files = new() { ["v1/content/news.json"] = At("stable", 0), ["v1/updates/alpha/news.json"] = At("alpha", 2) };
        async Task<string?> Fetch(string? token, IReadOnlyCollection<string>? channels) =>
            (await News.FetchAsync(file, Trust(), new RouteFailover(new ChannelEdge(files), [new("https://api.scskiller.com/")]), token, channels))?.Items.Single().Id;
        string? Kept(IReadOnlyCollection<string>? channels) => News.Cached(file, Trust(), channels)?.Items.Single().Id;

        Assert.Equal("alpha", await Fetch("tok", News.Channels(UpdateChannels.Alpha)));
        Assert.Equal("alpha", Kept(null));   // a start before the account answers keeps what was read
        Assert.Equal("alpha", await Fetch(null, null));   // and so does a check the account didn't answer

        Assert.Equal("stable", Kept(News.Channels(UpdateChannels.Stable)));   // switched to stable: no fetch needed
        Assert.False(File.Exists(Path.Combine(_dir, "news@alpha.json")));
        Assert.Equal("stable", Kept(null));   // and the next start
        Assert.Equal("stable", await Fetch("tok", []));   // stable's older copy: its own channel never saw a newer one

        Assert.Equal("alpha", await Fetch("tok", News.Channels(UpdateChannels.Alpha)));   // back on alpha: the same copy again
        Assert.Equal("stable", await Fetch(null, []));   // signed out, the alpha flag gone with it
        File.Delete(file);
        Assert.Null(Kept([]));   // no copy of a channel read: no news
    }

    [Fact]
    public void ReadAndDismissed_ArePersisted()
    {
        var items = News.TryParse(Doc(Item("a"), Item("b"), Item("c")))!.Items;
        Assert.Equal(3, NewsState.Of(new AppStore(_dir), items).Unread.Count);
        string[] feed = ["a", "b", "c"];
        NewsState.MarkRead(new AppStore(_dir), ["a", "b"], feed);
        NewsState.Dismiss(new AppStore(_dir), "c", feed);
        var (shown, unread) = NewsState.Of(new AppStore(_dir), items);
        Assert.Equal(["a", "b"], shown.Select(i => i.Id));
        Assert.Empty(unread);
        Assert.Equal(["d"], NewsState.Of(new AppStore(_dir), [.. items, .. News.TryParse(Doc(Item("d")))!.Items]).Unread);
    }

    [Fact]
    public void ReadState_OfAnItemStillInTheFeed_SurvivesPruning()
    {
        var store = new AppStore(_dir);
        // one item stays while the other 49 are replaced in five revisions: the sixth opening still finds it read
        for (var rev = 0; rev < 6; rev++)
        {
            string[] feed = ["kept", .. Enumerable.Range(0, 49).Select(i => $"r{rev}-{i}")];
            var items = News.TryParse(Doc([.. feed.Select(id => Item(id))]))!.Items;
            if (rev > 0) Assert.DoesNotContain("kept", NewsState.Of(store, items).Unread);
            NewsState.MarkRead(store, feed, feed);
        }
        Assert.Equal(50 + 200, store.LoadNewsSeen().Read!.Length);   // the feed's, then the newest 200 others
    }

    const string Changelog = """
        # Changelog

        ## [Unreleased]

        - Not yet.

        ## [1.3.0]

        ### Added

        - One line.
        - Two
          lines with `code`.

        ### Fixed

        - A fix. With details after it.

        ## [1.2.3] - 2026-10-04

        - Older.
        """;

    [Fact]
    public void WhatsNew_ReadsTheVersionsSection()
    {
        Assert.Equal(["One line.", "Two lines with `code`.", "A fix."], WhatsNew.Section(Changelog, "1.3.0")!.Bullets);
        Assert.Equal(["Older."], WhatsNew.Section(Changelog, "1.2.3")!.Bullets);
        Assert.Null(WhatsNew.Section(Changelog, "1.4.0"));
        Assert.NotNull(WhatsNew.Section(WhatsNew.Changelog, "1.3.0"));   // embedded at build
        Assert.NotNull(WhatsNew.ForAbout(new(0, 0, 0, "internal.0")));   // a dev build: the newest released section
    }

    [Fact]
    public void WhatsNew_Spans_BoldCodeLinkAndAStrayAsterisk()
    {
        Assert.Equal([new("Partly compiled", SpanKind.Bold), new(" replaces "), new("--memory", SpanKind.Code), new(" and "),
            new("docs", SpanKind.Link, "https://x.y/z")], WhatsNew.Spans("**Partly compiled** replaces `--memory` and [docs](https://x.y/z)"));
        Assert.Equal([new("2 * 3 and **open")], WhatsNew.Spans("2 * 3 and **open"));
    }

    [Theory]
    [InlineData("see [x](http://a.b/c) now")]
    [InlineData("see [x](https://:) now")]
    [InlineData("see [x](ftp://a.b) now")]
    public void WhatsNew_Spans_LeavesAnUnsafeOrBrokenLinkAsText(string text) =>
        Assert.Equal([new Span(text)], WhatsNew.Spans(text));

    [Fact]
    public void WhatsNew_ShowsOncePerVersion_NeverOnAFreshInstall()
    {
        var store = new AppStore(_dir);
        Assert.Null(WhatsNew.AtStart(store, firstRun: true, AppVersion.Parse("1.3.0")!, Changelog));   // the welcome shows instead
        Assert.Null(WhatsNew.AtStart(store, firstRun: false, AppVersion.Parse("1.3.0")!, Changelog));

        var updated = new AppStore(Path.Combine(_dir, "updated"));
        Directory.CreateDirectory(updated.DataDir);
        Assert.Null(WhatsNew.AtStart(updated, false, AppVersion.Parse("1.2.3-beta.1")!, "## [1.2.4]\n- x"));   // no section: nothing
        Assert.Equal("1.3.0", WhatsNew.AtStart(updated, false, AppVersion.Parse("1.3.0-beta.1")!, Changelog)!.Version);
        Assert.Null(WhatsNew.AtStart(updated, false, AppVersion.Parse("1.3.0")!, Changelog));   // the same X.Y.Z
        Assert.Equal("1.2.3", WhatsNew.AtStart(updated, false, AppVersion.Parse("1.2.3")!, Changelog)!.Version);   // another version again
    }
}
