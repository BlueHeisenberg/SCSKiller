using System.Text.Json.Nodes;
using SCSKiller.Core;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Platform;

public class BootPrecompilesTests
{
    static Game G(string id) => new(id, "Any Name", Store.Other, @"X:\none", @"X:\none\game.exe");

    static string One(Action<JsonObject> edit)
    {
        var e = new JsonObject { ["name"] = "Some Game", ["ids"] = new JsonArray("steam:42"), ["source"] = "https://example.org/a", ["date"] = "2026-01-01" };
        edit(e);
        return new JsonObject { ["games"] = new JsonArray(e) }.ToJsonString();
    }

    [Fact]
    public void Embedded_list_matches_by_store_id_only()
    {
        var list = BootPrecompiles.Embedded;
        Assert.True(list.Has(G("steam:2420110")));   // Horizon Forbidden West Complete Edition
        Assert.False(list.Has(new Game("epic:x", "Horizon Forbidden West Complete Edition", Store.Other, @"X:\none", @"X:\none\game.exe")));
        Assert.False(list.Has(G("steam:1888930")));   // The Last of Us Part I: NaughtyDogReader reads its shaders
        Assert.False(list.Has(G("steam:1774580")));
    }

    [Fact]
    public void TryParse_rejects_the_whole_document_on_any_bad_entry()
    {
        Assert.True(BootPrecompiles.TryParse(One(_ => { }))!.Has(G("steam:42")));
        Assert.NotNull(BootPrecompiles.TryParse(One(e => e["ids"] = new JsonArray("epic:a-b", "xbox:Bethesda.Project_3275kfvn8vcwc"))));
        foreach (var bad in new Action<JsonObject>[]
                 {
                     e => e["ids"] = new JsonArray(), e => e["ids"] = new JsonArray("steam:abc"), e => e["ids"] = new JsonArray("origin:1"),
                     e => e["ids"] = new JsonArray("steam:1", null), e => e["ids"] = new JsonArray(Enumerable.Range(1, 9).Select(i => (JsonNode?)$"steam:{i}").ToArray()),
                     e => e["source"] = "http://example.org/", e => e["source"] = StutterList.OwnMeasurement, e => e["source"] = "https://example.org/" + new string('x', 500),
                     e => e["name"] = " ", e => e["name"] = new string('x', 101), e => e["date"] = "2026-13-01",
                     e => e.Remove("source"), e => e["severity"] = "severe", e => e["reason"] = "r",
                 })
            Assert.Null(BootPrecompiles.TryParse(One(bad)));
        foreach (var bad in new[] { "", "{}", "null", """{"games":[]}""", """{"games":[null]}""", """{"games":[],"other":1}""" })
            Assert.Null(BootPrecompiles.TryParse(bad));
    }
}
