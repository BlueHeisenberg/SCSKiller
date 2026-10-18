using System.Net;
using System.Text.Json.Nodes;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Platform;

/// <summary>The known-stutter list: lookup by store id, then by name; the Library's "Recommended" order; validation of
/// the server's untrusted copy; server copy over the cached one over the embedded one.</summary>
public class StutterListTests : IDisposable
{
    static readonly string Valid = new StreamReader(typeof(StutterList).Assembly.GetManifestResourceStream("SCSKiller.Core.Games.known-stutter.json")!).ReadToEnd();
    readonly string _dir = Directory.CreateTempSubdirectory("scskiller-stutter-").FullName;
    string Cache => Path.Combine(_dir, "known-stutter.json");

    public void Dispose() => Directory.Delete(_dir, true);

    static Game G(string id, string name) => new(id, name, Store.Other, @"X:\none", @"X:\none\game.exe");

    static GameState S(string id, string name, GameStatus status, KnownStutter? k) =>
        new(G(id, name), null, AntiCheat.None, status, "", null, null, null, null, null, null, null, false, null, KnownStutter: k);

    static string One(Action<JsonObject> edit)
    {
        var e = new JsonObject
        {
            ["name"] = "Some Game", ["ids"] = new JsonArray("steam:42"), ["severity"] = "severe", ["reason"] = "r",
            ["source"] = "https://example.org/a", ["date"] = "2026-01-01",
        };
        edit(e);
        return new JsonObject { ["games"] = new JsonArray(e) }.ToJsonString();
    }

    [Fact]
    public void Embedded_list_matches_by_store_id_then_by_name()
    {
        var list = StutterList.Embedded;
        Assert.True(list.Count >= 15);
        Assert.Equal(StutterSeverity.Severe, list.Find(G("ea:198300", "anything"))!.Severity);                  // Jedi Survivor by EA id
        Assert.NotNull(list.Find(G("xbox:BethesdaSoftworks.ProjectAltar_3275kfvn8vcwc", "Oblivion Remastered")));
        Assert.NotNull(list.Find(G("epic:someapp", "STAR WARS Jedi: Survivor™")));                              // name: letters and digits only
        Assert.NotNull(list.Find(G("xbox:Unknown.Family_x", "ATOMIC HEART")));
        Assert.Null(list.Find(G("steam:1", "ELDEN RING NIGHTREIGN")));                                           // not a prefix match
        Assert.Null(list.Find(G("steam:1817230", "Hi-Fi RUSH")));
        var warThunder = list.Find(G("steam:236390", "?"));
        Assert.NotNull(warThunder);
        Assert.Same(warThunder, list.Find(G("gaijin:warthunder", "?")));
        Assert.All(new[] { "steam:990080", "steam:1286680", "steam:1245620", "steam:2909400", "steam:2623190", "steam:668580" },
            id => Assert.NotNull(list.Find(G(id, "?"))));
        var townfall = list.Find(G("steam:1636440", "?"))!;
        Assert.Equal(StutterSeverity.Severe, townfall.Severity);
        Assert.StartsWith("https://", townfall.Source);
        var eldenRing = list.Find(G("steam:1245620", "?"))!;   // most of its hitches aren't shader compiles
        Assert.Equal(StutterSeverity.Moderate, eldenRing.Severity);
        Assert.StartsWith("https://www.digitalfoundry.net/", eldenRing.Source);
    }

    [Fact]
    public void TryParse_reads_a_replacement_list()
    {
        var list = StutterList.TryParse(One(_ => { }))!;
        Assert.Equal(new KnownStutter(StutterSeverity.Severe, "r", "https://example.org/a", "2026-01-01"), list.Find(G("steam:42", "x")));
        Assert.NotNull(list.Find(G("gog:1", "some game")));
        Assert.Equal(StutterSeverity.Moderate, StutterList.TryParse(One(e => e["severity"] = "moderate"))!.Find(G("steam:42", "x"))!.Severity);
        Assert.NotNull(StutterList.TryParse(One(e => e["source"] = StutterList.OwnMeasurement)));
        Assert.NotNull(StutterList.TryParse(One(e => e["ids"] = new JsonArray())));
        Assert.NotNull(StutterList.TryParse(One(e => e["ids"] = new JsonArray("xbox:Bethesda.Project_3275kfvn8vcwc", "epic:a-b", "ea:198300", "purple:A2_WW_L_GA_PURPLE"))));
        Assert.NotNull(StutterList.TryParse(One(e => e["ids"] = new JsonArray("hoyoplay:hk4e_global", "gaijin:warthunder", "riot:valorant.live"))));
    }

    [Fact]
    public void TryParse_rejects_the_whole_document_on_any_bad_entry()
    {
        foreach (var bad in new Action<JsonObject>[]
                 {
                     e => e["severity"] = "Severe", e => e["severity"] = "mild", e => e["severity"] = null,
                     e => e["source"] = "http://example.org/", e => e["source"] = "https://user@example.org/", e => e["source"] = "https://example.org:8443/",
                     e => e["source"] = "javascript:alert(1)", e => e["source"] = "our own measurement", e => e["source"] = "",
                     e => e["ids"] = new JsonArray("steam:abc"), e => e["ids"] = new JsonArray("origin:1"), e => e["ids"] = new JsonArray("steam:"),
                     e => e["ids"] = new JsonArray("xbox:a/b"), e => e["ids"] = new JsonArray("steam:1", null), e => e["ids"] = "steam:1",
                     e => e["name"] = "™ :: ™", e => e["name"] = " ", e => e["name"] = "Game\u001b[31m", e => e["reason"] = "line\nbreak",
                     e => e["date"] = "26-09-2026", e => e["date"] = "2026-13-01",
                     e => e.Remove("reason"), e => e.Remove("ids"), e => e["extra"] = 1, e => e["name"] = 5,
                 })
            Assert.Null(StutterList.TryParse(One(bad)));
        foreach (var bad in new[] { "", "{}", "[]", "null", """{"games":[]}""", """{"games":null}""", """{"games":[null]}""", "{\"games\":[", """{"games":[],"other":1}""" })
            Assert.Null(StutterList.TryParse(bad));
    }

    [Fact]
    public void TryParse_rejects_oversized_documents_and_fields()
    {
        Assert.NotNull(StutterList.TryParse(One(e => e["name"] = new string('x', 100))));
        Assert.Null(StutterList.TryParse(One(e => e["name"] = new string('x', 101))));
        Assert.NotNull(StutterList.TryParse(One(e => e["reason"] = new string('x', 300))));
        Assert.Null(StutterList.TryParse(One(e => e["reason"] = new string('x', 301))));
        Assert.Null(StutterList.TryParse(One(e => e["source"] = "https://example.org/" + new string('x', 500))));
        Assert.Null(StutterList.TryParse(One(e => e["ids"] = new JsonArray(Enumerable.Range(1, 9).Select(i => (JsonNode?)$"steam:{i}").ToArray()))));

        JsonArray Games(int n) => new(Enumerable.Range(1, n).Select(i => (JsonNode?)JsonNode.Parse(One(e => e["ids"] = new JsonArray($"steam:{i}")))!["games"]![0]!.DeepClone()).ToArray());
        Assert.NotNull(StutterList.TryParse(new JsonObject { ["games"] = Games(StutterList.MaxGames) }.ToJsonString()));
        Assert.Null(StutterList.TryParse(new JsonObject { ["games"] = Games(StutterList.MaxGames + 1) }.ToJsonString()));
        Assert.Null(StutterList.TryParse(One(_ => { })[..^1] + new string(' ', StutterList.MaxBytes) + "}"));   // valid JSON, over 64 KB
    }

    sealed class Server(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Asked;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(answer(request.RequestUri!.ToString()));
        }
    }

    static RouteFailover Serving(string? body) => new(new Server(url =>
        url != "https://api.scskiller.com/v1/content/known-stutter.json" ? throw new InvalidOperationException(url)
        : body == null ? throw new HttpRequestException("no network") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }),
        [new("https://api.scskiller.com/")]);

    [Fact]
    public async Task Server_copy_over_cached_copy_over_embedded()
    {
        Assert.Null(StutterList.Cached(Cache));   // nothing cached: the embedded list stays

        var served = One(e => e["name"] = "From Server");
        var fresh = await StutterList.FetchAsync(Cache, Serving(served));
        Assert.NotNull(fresh!.Find(G("steam:42", "x")));
        Assert.Equal(served, File.ReadAllText(Cache));                                  // kept for the next start
        Assert.NotNull(StutterList.Cached(Cache)!.Find(G("steam:1", "From Server")));

        var newer = One(e => e["ids"] = new JsonArray("steam:43"));
        Assert.NotNull((await StutterList.FetchAsync(Cache, Serving(newer)))!.Find(G("steam:43", "x")));   // a fresh copy replaces the cached one
        Assert.Equal(newer, File.ReadAllText(Cache));
    }

    [Fact]
    public async Task Offline_or_invalid_server_copy_keeps_the_cached_copy()
    {
        var good = One(_ => { });
        File.WriteAllText(Cache, good);

        Assert.Null(await StutterList.FetchAsync(Cache, Serving(null)));                                   // offline
        Assert.Null(await StutterList.FetchAsync(Cache, Serving(One(e => e["severity"] = "extreme"))));   // invalid: rejected whole
        Assert.Null(await StutterList.FetchAsync(Cache, Serving("<html>")));
        Assert.Equal(good, File.ReadAllText(Cache));
        Assert.NotNull(StutterList.Cached(Cache)!.Find(G("steam:42", "x")));

        File.WriteAllText(Cache, "{\"games\":[{\"name\":\"x\"");   // a damaged cache: the embedded list is used
        Assert.Null(StutterList.Cached(Cache));
    }

    [Fact]
    public void Recommended_is_listed_games_to_do_first_then_warmed_severe_first_then_by_name()
    {
        KnownStutter severe = new(StutterSeverity.Severe, "", "", ""), moderate = new(StutterSeverity.Moderate, "", "", "");
        var games = new[]
        {
            S("a", "Beta", GameStatus.Ready, moderate),
            S("b", "Alpha", GameStatus.NeedsRecording, moderate),
            S("c", "Zeta", GameStatus.Stale, severe),
            S("d", "Warmed", GameStatus.Warmed, severe),          // compiled: stays, after the ones still to do
            S("e", "Blocked", GameStatus.Unsupported, severe),    // anti-cheat / unsupported
            S("f", "Unlisted", GameStatus.Ready, null),
        };
        Assert.Equal(["Zeta", "Alpha", "Beta", "Warmed"], StutterList.Recommended(games).Select(s => s.Game.Name));
    }
}
