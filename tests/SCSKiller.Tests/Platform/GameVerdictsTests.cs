using System.Text.Json.Nodes;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Platform;

public class GameVerdictsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-verdicts-test-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly (string Seed, string Public) Key = FeedTrust.NewKey(), Other = FeedTrust.NewKey();
    static readonly DateTimeOffset T = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    const string Precompiles = "Its engine prepares shaders at startup";

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    static Game G(string id, string? version = null) => new(id, "Any Name", Store.Other, @"X:\none", @"X:\none\game.exe", version);
    static EngineInfo Ue(string version) => new("Unreal", version, null, "D3D12", false, null);

    static JsonObject Entry(string verdict = GameVerdicts.NoStutterVerdict, string id = "steam:42", string reason = "engine-precompiles") => new()
    {
        ["name"] = "Some Game", ["ids"] = new JsonArray(id), ["verdict"] = verdict, ["reason"] = reason,
        ["source"] = "SCSKiller measurement: the first launch compiled nothing slow", ["date"] = "2026-01-01",
    };

    static string Doc(params JsonObject[] entries) => new JsonObject { ["games"] = new JsonArray(entries) }.ToJsonString();

    static JsonObject With(Action<JsonObject> edit)
    {
        var e = Entry();
        edit(e);
        return e;
    }

    ContentTrust Trust() => new(new AppStore(_dir), new Dictionary<string, string> { ["rules-a"] = Key.Public });
    static string Signed(string body, string seed, string name = GameVerdicts.Name) => ContentTrust.Sign(Convert.FromBase64String(seed), "rules-a", name, body, T);

    [Fact]
    public void Embedded_list_is_valid_and_shares_no_game_with_the_known_stutter_list()
    {
        var list = GameVerdicts.Embedded;
        var games = System.Text.Json.JsonDocument.Parse(ContentFile.Embedded("SCSKiller.Core.Games.game-verdicts.json")).RootElement.GetProperty("games");
        Assert.Equal(list.Count, games.GetArrayLength());
        foreach (var id in games.EnumerateArray().SelectMany(g => g.GetProperty("ids").EnumerateArray().Select(i => i.GetString()!)))
            Assert.Null(StutterList.Embedded.Find(G(id)));
    }

    [Fact]
    public void A_verdict_matches_by_store_id_only()
    {
        var list = GameVerdicts.TryParse(Doc(Entry(GameVerdicts.UnsupportedYetVerdict, "steam:999999901", GameVerdicts.OtherReason).Also(e => e["note"] = "An app update is needed for support")))!;
        Assert.Equal(new GameVerdicts.Verdict(GameVerdicts.UnsupportedYetVerdict, "An app update is needed for support"),
            list.Unsupported(G("steam:999999901"), null));
        Assert.Null(list.NoStutter(G("steam:999999901"), null));
        Assert.Null(list.Unsupported(new Game("epic:x", "Some Game", Store.Other, @"X:\none", @"X:\none\game.exe"), null));
    }

    /// <summary>1.2.3 reads only known-stutter.json, strictly: the verdicts live in their own file, which it never asks for and
    /// would refuse whole.</summary>
    [Fact]
    public void The_known_stutter_list_stays_as_released_clients_read_it()
    {
        Assert.NotNull(StutterList.TryParse(ContentFile.Embedded("SCSKiller.Core.Games.known-stutter.json")));
        Assert.Null(StutterList.TryParse(ContentFile.Embedded("SCSKiller.Core.Games.game-verdicts.json")));
    }

    [Fact]
    public void Each_verdict_is_its_own()
    {
        var list = GameVerdicts.TryParse(Doc(Entry(), Entry(GameVerdicts.UnsupportedYetVerdict, "epic:yet", "engine-unreadable"),
            Entry(GameVerdicts.UnsupportedVerdict, "epic:never", "anti-cheat")))!;
        Assert.Equal((Precompiles, (GameVerdicts.Verdict?)null), (list.NoStutter(G("steam:42"), null), list.Unsupported(G("steam:42"), null)));
        Assert.Equal(new GameVerdicts.Verdict(GameVerdicts.UnsupportedYetVerdict, "SCSKiller can't read this game's shaders yet"), list.Unsupported(G("epic:yet"), null));
        Assert.Equal(new GameVerdicts.Verdict(GameVerdicts.UnsupportedVerdict, "Its anti-cheat doesn't allow it"), list.Unsupported(G("epic:never"), null));
        Assert.Null(list.NoStutter(G("epic:never"), null));
    }

    [Fact]
    public void A_reason_code_shows_the_apps_line_and_an_unknown_one_the_note_else_one_for_the_verdict()
    {
        var list = GameVerdicts.TryParse(Doc(
            With(e => e["note"] = "Ignored next to a known code"),
            Entry(GameVerdicts.UnsupportedVerdict, "epic:a", "renderer-x").Also(e => e["note"] = "Its renderer crashes the recorder"),
            Entry(GameVerdicts.UnsupportedYetVerdict, "epic:b", "renderer-x"),
            Entry(GameVerdicts.NoStutterVerdict, "epic:c", "renderer-x"),
            Entry(GameVerdicts.UnsupportedVerdict, "epic:d", GameVerdicts.OtherReason).Also(e => e["note"] = "One-off"),
            Entry(GameVerdicts.UnsupportedVerdict, "epic:e", GameVerdicts.OtherReason)))!;
        Assert.Equal(Precompiles, list.NoStutter(G("steam:42"), null));
        Assert.Equal("Its renderer crashes the recorder", list.Unsupported(G("epic:a"), null)!.Text);
        Assert.Equal("SCSKiller can't compile this game", list.Unsupported(G("epic:b"), null)!.Text);
        Assert.Equal("This game prepares its shaders itself", list.NoStutter(G("epic:c"), null));
        Assert.Equal("One-off", list.Unsupported(G("epic:d"), null)!.Text);
        Assert.Equal("SCSKiller can't compile this game", list.Unsupported(G("epic:e"), null)!.Text);
        Assert.All(GameVerdicts.Reasons.Values, t => Assert.True(t.Length <= 60 && !t.Contains(';')));
    }

    [Fact]
    public void Engines_and_builds_limit_an_entry()
    {
        var scoped = GameVerdicts.TryParse(Doc(With(e => { e["engines"] = new JsonArray("Unreal 5.7"); e["builds"] = new JsonArray("123"); })))!;
        Assert.NotNull(scoped.NoStutter(G("steam:42", "123"), Ue("5.7.4")));
        Assert.NotNull(scoped.NoStutter(G("steam:42", "123"), Ue("5.7")));
        Assert.Null(scoped.NoStutter(G("steam:42", "123"), Ue("5.70")));
        Assert.Null(scoped.NoStutter(G("steam:42", "124"), Ue("5.7")));
        Assert.Null(scoped.NoStutter(G("steam:42"), Ue("5.7")));
        Assert.Null(scoped.NoStutter(G("steam:42", "123"), null));
    }

    [Fact]
    public void Later_additions_skip_only_the_entries_they_touch()
    {
        var doc = new JsonObject
        {
            ["games"] = new JsonArray(Entry(), With(e => e["verdict"] = "stutter"), With(e => e["severity"] = "severe"),
                With(e => e["ids"] = new JsonArray("steam:7")), With(e => e["reason"] = "Engine Precompiles"), With(e => e.Remove("reason")),
                With(e => e["note"] = new string('x', 121)), With(e => e.Remove("source")), With(e => e["engines"] = new JsonArray()),
                With(e => e["builds"] = null), With(e => e["date"] = "2026-13-01")),
            ["signature"] = "later", ["about"] = 3,
        }.ToJsonString();
        var list = GameVerdicts.TryParse(doc)!;
        Assert.Equal(3, list.Count);   // the first, the other id, and builds: null
        Assert.NotNull(list.NoStutter(G("steam:7"), null));
        Assert.Equal(0, GameVerdicts.TryParse("""{"games":[]}""")!.Count);
        Assert.Equal(0, GameVerdicts.TryParse("""{"games":[null, 1, "x"]}""")!.Count);
        Assert.Equal(1, GameVerdicts.TryParse(Doc(Entry(id: "newstore:abc")))!.Count);
    }

    [Fact]
    public void An_empty_source_is_accepted_but_not_a_missing_blank_or_bad_one()
    {
        Assert.Equal(Precompiles, GameVerdicts.TryParse(Doc(With(e => e["source"] = "")))!.NoStutter(G("steam:42"), null));
        foreach (var bad in new[] { With(e => e.Remove("source")), With(e => e["source"] = null), With(e => e["source"] = " "),
                     With(e => e["source"] = new string('x', 501)), With(e => e["source"] = "a\u0007b") })
            Assert.Equal(0, GameVerdicts.TryParse(Doc(bad))!.Count);
    }

    [Fact]
    public void A_document_without_a_games_array_is_refused()
    {
        foreach (var bad in new[] { "", "{}", "null", "[]", """{"games":{}}""", "{", Signed(Doc(Entry()), Key.Seed),
                     Doc(Enumerable.Range(0, GameVerdicts.MaxGames + 1).Select(_ => Entry()).ToArray()) })
            Assert.Null(GameVerdicts.TryParse(bad));
    }

    [Fact]
    public void Only_a_list_signed_with_a_pinned_key_for_its_name_is_used()
    {
        var body = Doc(Entry());
        Assert.Equal(Precompiles, Trust().Accept(GameVerdicts.Name, Signed(body, Key.Seed), GameVerdicts.TryParse)!.NoStutter(G("steam:42"), null));

        var t = Trust();
        Assert.Null(t.Accept(GameVerdicts.Name, Signed(body, Other.Seed), GameVerdicts.TryParse));
        Assert.Contains("bad signature", t.Rejected);
        var tampered = JsonNode.Parse(Signed(body, Key.Seed))!.AsObject();
        tampered["body"] = Doc(Entry(GameVerdicts.UnsupportedVerdict));
        Assert.Null(t.Accept(GameVerdicts.Name, tampered.ToJsonString(), GameVerdicts.TryParse));
        Assert.Contains("doesn't match", t.Rejected);
        Assert.Null(t.Accept(GameVerdicts.Name, body, GameVerdicts.TryParse));   // unsigned
        Assert.Equal("malformed envelope", t.Rejected);
        Assert.Null(t.Accept(GameVerdicts.Name, Signed(body, Key.Seed, "news"), GameVerdicts.TryParse));
        Assert.Null(new ContentTrust(new AppStore(_dir), ContentTrust.RulesKeys).Accept(GameVerdicts.Name, Signed(body, Key.Seed), GameVerdicts.TryParse));   // the pinned rules-a refuses a test key
    }
}

static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject o, Action<JsonObject> edit)
    {
        edit(o);
        return o;
    }
}
