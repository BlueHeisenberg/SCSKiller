using System.Text.Json.Nodes;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Platform;

public class MaximumModeTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-maximum-test-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly (string Seed, string Public) Key = FeedTrust.NewKey();
    static readonly DateTimeOffset T = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    static Game G(string id) => new(id, "Any Name", Store.Other, @"X:\none", @"X:\none\game.exe");

    static JsonObject Entry(params string[] vendors) => new()
    {
        ["name"] = "SILENT HILL: Townfall", ["ids"] = new JsonArray("steam:42"), ["vendors"] = new JsonArray(vendors.Select(v => (JsonNode)v).ToArray()),
        ["source"] = "players' A/B on Discord", ["date"] = "2026-10-01",
    };

    static JsonObject With(Action<JsonObject> edit, params string[] vendors)
    {
        var e = Entry(vendors.Length > 0 ? vendors : ["amd"]);
        edit(e);
        return e;
    }

    static string Doc(params JsonObject[] entries) => new JsonObject { ["about"] = null, ["games"] = new JsonArray(entries) }.ToJsonString();

    [Fact]
    public void The_repos_body_is_embedded_and_suggests_nothing()
    {
        Assert.Equal(0, MaximumMode.Embedded.Count);
        Assert.Equal(ContentFile.Embedded("SCSKiller.Core.Games.maximum-mode.json").Replace("\r\n", "\n"),
            File.ReadAllText(Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.Core", "Games", "maximum-mode.json")).Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_game_is_suggested_only_on_a_vendor_its_entry_lists_as_written()
    {
        var list = MaximumMode.TryParse(Doc(With(e => e["note"] = "Removed the last stutters", "amd"), With(e => e["ids"] = new JsonArray("epic:abc", "steam:7"), "amd", "nvidia")))!;
        Assert.Equal(new MaximumMode.Suggestion("Removed the last stutters"), list.For(G("steam:42"), "amd"));
        Assert.Null(list.For(G("steam:42"), "nvidia"));
        Assert.Null(list.For(G("steam:42"), "AMD"));
        Assert.Null(list.For(G("steam:42"), null));   // a vendor without a name: Intel, unknown
        Assert.Equal(new MaximumMode.Suggestion(null), list.For(G("steam:7"), "nvidia"));
        Assert.NotNull(list.For(G("EPIC:ABC"), "amd"));   // ids match as game verdicts do
        Assert.Null(list.For(G("steam:8"), "amd"));
    }

    [Fact]
    public void An_entry_this_version_finds_invalid_is_skipped_and_unknown_top_level_members_ignored()
    {
        var doc = new JsonObject
        {
            ["games"] = new JsonArray(Entry("amd"), Entry("amd", "nvidia"), Entry("AMD"), Entry("intel"), Entry("amd", "amd"), Entry("amd", "nvidia", "amd"),
                Entry(), With(e => e["vendors"] = null), With(e => e.Remove("vendors")), With(e => e["vendors"] = "amd"),
                With(e => e["severity"] = 3), With(e => e["ids"] = new JsonArray()), With(e => e["ids"] = new JsonArray("nostore")),
                With(e => e["ids"] = new JsonArray(Enumerable.Range(0, 9).Select(i => (JsonNode)$"steam:{i}").ToArray())),
                With(e => e["name"] = " "), With(e => e["note"] = ""), With(e => e["note"] = new string('x', 121)), With(e => e["source"] = null),
                With(e => e["source"] = "a\u0007b"), With(e => e["date"] = "2026-13-01"), With(e => e["date"] = "01/10/2026")),
            ["about"] = "later", ["signature"] = 1,
        }.ToJsonString();
        Assert.Equal(2, MaximumMode.TryParse(doc)!.Count);
        Assert.Equal(1, MaximumMode.TryParse(Doc(With(e => e["note"] = null)))!.Count);   // an explicit null note: no note
        Assert.Equal(1, MaximumMode.TryParse(Doc(With(e => e["source"] = "")))!.Count);
        Assert.Equal(0, MaximumMode.TryParse("""{"games":[null, 1, "x"]}""")!.Count);
    }

    [Fact]
    public void A_document_without_a_games_array_is_refused()
    {
        foreach (var bad in new[] { "", "{}", "null", "[]", """{"games":{}}""", """{"games":null}""", "{",
                     Doc(Enumerable.Range(0, GameVerdicts.MaxGames + 1).Select(_ => Entry("amd")).ToArray()) })
            Assert.Null(MaximumMode.TryParse(bad));
    }

    [Fact]
    public void Only_a_body_signed_for_maximum_mode_is_accepted()
    {
        var trust = new ContentTrust(new AppStore(_dir), new Dictionary<string, string> { ["rules-a"] = Key.Public });
        var body = Doc(Entry("amd"));
        string Signed(string name) => ContentTrust.Sign(Convert.FromBase64String(Key.Seed), "rules-a", name, body, T);
        Assert.Equal(1, trust.Accept(MaximumMode.Name, Signed(MaximumMode.Name), MaximumMode.TryParse)!.Count);
        Assert.Null(trust.Accept(MaximumMode.Name, Signed(GameVerdicts.Name), MaximumMode.TryParse));
        Assert.Null(trust.Accept(MaximumMode.Name, body, MaximumMode.TryParse));   // unsigned
    }
}
