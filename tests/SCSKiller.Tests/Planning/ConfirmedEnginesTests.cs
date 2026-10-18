using System.Net;
using System.Text.Json.Nodes;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Planning;

/// <summary>The confirmed-engines list: which versions and forks the embedded copy confirms, validation of a server
/// copy (rejected whole), and the server copy only adding to the embedded one.</summary>
public class ConfirmedEnginesTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("scsk-confirmed-").FullName;
    string Cache => Path.Combine(_dir, "confirmed-engines.json");
    public void Dispose() => Directory.Delete(_dir, true);

    static EngineInfo E(string version, string? fork = null, string family = "Unreal") => new(family, version, fork, "D3D12", false, null);

    static string One(Action<JsonObject> edit)
    {
        var e = new JsonObject { ["version"] = "5.3", ["game"] = "Some Game", ["evidence"] = "its recording" };
        edit(e);
        return new JsonObject { ["engines"] = new JsonArray(e) }.ToJsonString();
    }

    [Theory]
    [InlineData("4.26", "GAME_FinalFantasy7Rebirth", true)]
    [InlineData("4.26", null, true)] [InlineData("4.27", null, true)]
    [InlineData("4.26", "GAME_StarWarsJediSurvivor", true)] [InlineData("4.27", "GAME_HogwartsLegacy", true)]
    [InlineData("5.1", null, true)] [InlineData("5.6", null, true)] [InlineData("4.21", null, false)]
    [InlineData("4.26", "GAME_StellarBlade", false)] [InlineData("5.1", "GAME_Palworld", false)]
    [InlineData("4.20", null, false)] [InlineData("4.25", null, true)] [InlineData("5.0", null, false)] [InlineData("5.2", null, false)]
    [InlineData("5.3", null, false)] [InlineData("5.4", null, true)] [InlineData("5.5", null, false)] [InlineData("5.7", null, false)]
    public void Embedded_list_confirms_the_engines_a_game_has_shown(string version, string? fork, bool confirmed)
    {
        Assert.Equal(confirmed, ConfirmedEngines.Embedded.Contains(E(version, fork)));
        Assert.Equal(confirmed, RootSig.Verified(E(version, fork)));
    }

    [Fact]
    public void Only_an_engine_with_a_rule_is_verified()
    {
        Assert.True(ConfirmedEngines.Embedded.Contains(E("5.6", family: "Unity")));   // the list is versions and forks only
        Assert.False(RootSig.Verified(E("5.6", family: "Unity")));
    }

    [Fact]
    public void A_server_copy_only_adds_to_the_embedded_list()
    {
        var list = ConfirmedEngines.TryParse(One(e => e["fork"] = "GAME_Some_Fork2"))!;
        Assert.True(list.Contains(E("5.3", "GAME_Some_Fork2")));
        Assert.False(list.Contains(E("5.3")));                                        // the fork, not stock
        Assert.True(list.Contains(E("4.26", "GAME_FinalFantasy7Rebirth")));           // embedded, not in this copy
        Assert.False(ConfirmedEngines.Embedded.Contains(E("5.3", "GAME_Some_Fork2")));
        Assert.NotNull(ConfirmedEngines.TryParse(One(e => e["fork"] = null)));
    }

    [Fact]
    public void Invalid_documents_are_rejected_whole()
    {
        foreach (var bad in new Action<JsonObject>[]
                 {
                     e => e["version"] = "5", e => e["version"] = "5.", e => e["version"] = ".5", e => e["version"] = "5.4.1", e => e["version"] = "5.x",
                     e => e["version"] = "12.345", e => e["version"] = 5.4, e => e.Remove("version"),
                     e => e["fork"] = "StellarBlade", e => e["fork"] = "GAME_", e => e["fork"] = "GAME_a b", e => e["fork"] = "GAME_" + new string('x', 60),
                     e => e["game"] = "", e => e["game"] = new string('x', 101), e => e.Remove("game"), e => e["game"] = "a\u001b[31m",
                     e => e["evidence"] = " ", e => e["evidence"] = new string('x', 301), e => e.Remove("evidence"),
                     e => e["extra"] = 1,
                 })
            Assert.Null(ConfirmedEngines.TryParse(One(bad)));
        foreach (var bad in new[] { "", "null", "[]", "{}", """{"engines":[]}""", """{"engines":null}""", """{"engines":[null]}""", "<html>",
                     One(_ => { })[..^1] + ""","extra":1}""", One(_ => { })[..^1] + ""","about":""}""" })
            Assert.Null(ConfirmedEngines.TryParse(bad));
        Assert.Null(ConfirmedEngines.TryParse(One(_ => { })[..^1] + new string(' ', ContentFile.MaxBytes) + "}"));   // valid JSON, over 64 KB
        JsonArray Engines(int n) => new(Enumerable.Range(0, n).Select(i => (JsonNode?)new JsonObject { ["version"] = "5.4", ["fork"] = $"GAME_F{i}", ["game"] = "g", ["evidence"] = "e" }).ToArray());
        Assert.NotNull(ConfirmedEngines.TryParse(new JsonObject { ["engines"] = Engines(ConfirmedEngines.MaxEngines) }.ToJsonString()));
        Assert.Null(ConfirmedEngines.TryParse(new JsonObject { ["engines"] = Engines(ConfirmedEngines.MaxEngines + 1) }.ToJsonString()));
    }

    sealed class Server(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request.RequestUri!.ToString()));
    }

    static RouteFailover Serving(string? body) => new(new Server(url =>
        url != "https://api.scskiller.com/v1/content/confirmed-engines.json" ? throw new InvalidOperationException(url)
        : body == null ? throw new HttpRequestException("no network") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }),
        [new("https://api.scskiller.com/")]);

    [Fact]
    public async Task Server_copy_is_cached_and_an_invalid_or_missing_one_keeps_it()
    {
        Assert.Null(ConfirmedEngines.Cached(Cache));
        var served = One(_ => { });
        Assert.True((await ConfirmedEngines.FetchAsync(Cache, Serving(served)))!.Contains(E("5.3")));
        Assert.Equal(served, File.ReadAllText(Cache));

        Assert.Null(await ConfirmedEngines.FetchAsync(Cache, Serving(null)));                           // offline
        Assert.Null(await ConfirmedEngines.FetchAsync(Cache, Serving(One(e => e["version"] = "x"))));   // invalid: rejected whole
        Assert.Equal(served, File.ReadAllText(Cache));
        Assert.True(ConfirmedEngines.Cached(Cache)!.Contains(E("5.3")));

        File.WriteAllText(Cache, "{\"engines\":[{\"version\":\"5.4\"");   // a damaged cache: the embedded list is used
        Assert.Null(ConfirmedEngines.Cached(Cache));
    }
}
