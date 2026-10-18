using System.Net;
using SCSKiller.Core;
using SCSKiller.Core.App;
using Xunit;

namespace SCSKiller.Tests.Platform;

public class ActiveCheckTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scsk-active-" + Guid.NewGuid().ToString("N"));
    readonly CommunityTests.Clock _clock = new() { Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };   // a Thursday
    readonly List<string> _bodies = [];
    HttpStatusCode _status = HttpStatusCode.NoContent;
    bool _enabled = true;

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    ActiveCheck Make(string version = "1.2.3", GpuVendor vendor = GpuVendor.Nvidia) => new(_dir, () => _enabled, vendor, AppVersion.Parse(version),
        new RouteFailover(new CommunityTests.Fake(r =>
        {
            Assert.Equal((HttpMethod.Post, "https://api.test.com/v1/active"), (r.Method, r.RequestUri!.ToString()));
            var headers = r.Headers.Concat(r.Content!.Headers).Select(h => h.Key).Except(["Content-Type", "Content-Length", "User-Agent"]).ToList();
            lock (_bodies) _bodies.Add(headers.Count == 0 ? r.Content.ReadAsStringAsync().Result : string.Join(' ', headers));   // no token, no id
            return CommunityTests.Ours(_status);
        }), [new("https://api.test.com/")], _clock), _clock);

    static string Body(bool first, bool week, bool month, string v = "1.2.3", string gpu = "nvidia") =>
        $$"""{"first":{{(first ? "true" : "false")}},"week":{{(week ? "true" : "false")}},"month":{{(month ? "true" : "false")}},"v":"{{v}}","gpu":"{{gpu}}"}""";

    async Task<string?> On(int year, int month, int day)
    {
        _clock.Now = new(year, month, day, 0, 30, 0, TimeSpan.Zero);
        var before = _bodies.Count;
        await Make().SendAsync();
        return _bodies.Count == before ? null : Assert.Single(_bodies[before..]);
    }

    [Fact]
    public async Task One_check_a_UTC_day_with_nothing_but_the_flags_the_version_and_the_vendor()
    {
        var check = Make();
        await Task.WhenAll(check.SendAsync(), check.SendAsync());
        await check.SendAsync();
        await Make().SendAsync();   // the app started again the same day
        _clock.Now += TimeSpan.FromHours(11.9);
        await Make().SendAsync();
        Assert.Equal("""{"first":true,"week":true,"month":true,"v":"1.2.3","gpu":"nvidia"}""", Assert.Single(_bodies));
        Assert.Equal("2026-10-01", File.ReadAllText(Path.Combine(_dir, "active-check.txt")));
        Assert.Equal(["active-check.txt"], Directory.GetFiles(_dir).Select(Path.GetFileName));

        _clock.Now += TimeSpan.FromHours(0.2);   // 00:06 UTC the next day
        await Make("1.2.3-beta.4+build.7", GpuVendor.Amd).SendAsync();
        await Make("1.2.3", GpuVendor.Intel).SendAsync();
        Assert.Equal(Body(false, false, false, "1.2.3-beta.4", "amd"), Assert.Single(_bodies[1..]));
    }

    [Fact]
    public async Task The_week_flag_follows_the_ISO_week_and_the_month_flag_the_calendar_month()
    {
        Assert.Equal(Body(true, true, true), await On(2026, 10, 1));
        Assert.Equal(Body(false, false, false), await On(2026, 10, 4));   // Sunday: the same ISO week
        Assert.Equal(Body(false, true, false), await On(2026, 10, 5));    // Monday
        Assert.Equal(Body(false, true, false), await On(2026, 10, 31));   // Saturday, weeks later
        Assert.Equal(Body(false, false, true), await On(2026, 11, 1));    // Sunday: a new month in the same week
        Assert.Equal(Body(false, true, false), await On(2026, 11, 2));
        Assert.Equal(Body(false, true, true), await On(2026, 12, 31));    // Thursday of ISO week 2026-W53
        Assert.Equal(Body(false, false, true), await On(2027, 1, 1));     // still 2026-W53
        Assert.Equal(Body(false, true, false), await On(2027, 1, 4));
        Assert.Null(await On(2027, 1, 3));                                // the clock set back: nothing until it passes the last check
        Assert.Equal(Body(false, true, true), await On(2028, 1, 3));      // a year on, the week number alone would match
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_refused_check_is_retried_later_with_the_same_flags(HttpStatusCode refusal)
    {
        Assert.Equal(Body(true, true, true), await On(2026, 10, 1));
        _clock.Now += TimeSpan.FromDays(4);   // Monday
        _status = refusal;
        var check = Make();
        await check.SendAsync();
        await check.SendAsync();   // not again within the hour
        Assert.Equal(2, _bodies.Count);
        Assert.Equal("2026-10-01", File.ReadAllText(Path.Combine(_dir, "active-check.txt")));

        _status = HttpStatusCode.NoContent;
        _clock.Now += TimeSpan.FromHours(1.1);
        await check.SendAsync();
        await check.SendAsync();
        Assert.Equal([Body(false, true, false), Body(false, true, false)], _bodies[1..]);

        _status = refusal;   // the first check ever, refused: still the first when it gets through
        File.Delete(Path.Combine(_dir, "active-check.txt"));
        await Make().SendAsync();
        _status = HttpStatusCode.NoContent;
        await Make().SendAsync();
        Assert.Equal([Body(true, true, true), Body(true, true, true)], _bodies[3..]);
    }

    [Fact]
    public async Task Switched_off_sends_nothing_and_keeps_nothing()
    {
        _enabled = false;
        await Make().SendAsync();
        Assert.Empty(_bodies);
        Assert.False(Directory.Exists(_dir));

        _enabled = true;
        await Make().SendAsync();
        Assert.Equal(Body(true, true, true), Assert.Single(_bodies));
    }

    [Theory]
    [InlineData("0.0.0-internal.0")]   // a build from a checkout (Directory.Build.props)
    [InlineData("1.2.3-internal.5")]
    [InlineData("1.2.3-dev")]
    public async Task Internal_and_dev_builds_send_nothing(string version)
    {
        await Make(version).SendAsync();
        Assert.Empty(_bodies);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void The_check_is_off_by_default_and_a_saved_value_is_read_back()
    {
        Assert.False(AppStore.DefaultSettings.ActiveCheck);
        foreach (var on in new[] { true, false })
        {
            var saved = System.Text.Json.JsonSerializer.Serialize(AppStore.DefaultSettings with { WelcomeSeen = true, ActiveCheck = on }, AppStore.Json);
            Assert.Equal(on, System.Text.Json.JsonSerializer.Deserialize<Settings>(saved, AppStore.Json)!.ActiveCheck);
        }
        // only settings from before the welcome existed lack it: the welcome shows again and asks
        var old = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(AppStore.DefaultSettings, AppStore.Json))!.AsObject();
        Assert.True(old.Remove("ActiveCheck") && old.Remove("WelcomeSeen"));
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<Settings>(old, AppStore.Json)! is { ActiveCheck: true } or { WelcomeSeen: true });
    }

    [Fact]
    public void Only_a_choice_allows_it_and_an_update_asks_once()
    {
        var fresh = AppStore.DefaultSettings;
        Assert.False(ActiveCheck.Allowed(fresh) || ActiveCheck.Asks(fresh));   // the welcome asks
        foreach (var on in new[] { true, false })
        {
            var welcomed = ActiveCheck.Answered(fresh, on) with { WelcomeSeen = true };
            Assert.Equal(on, ActiveCheck.Allowed(welcomed));
            Assert.False(ActiveCheck.Asks(welcomed));   // a new install never sees the question
        }

        // a settings.json from before the choice, with the check on: asked, and off until then
        var saved = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(fresh with { WelcomeSeen = true, ActiveCheck = true }, AppStore.Json))!.AsObject();
        Assert.True(saved.Remove("ActiveCheckAsked"));
        var updated = System.Text.Json.JsonSerializer.Deserialize<Settings>(saved, AppStore.Json)!;
        Assert.True(ActiveCheck.Asks(updated));
        Assert.False(ActiveCheck.Allowed(updated));
        Assert.True(ActiveCheck.Asks(updated with { ActiveCheck = false }));
        var kept = ActiveCheck.Answered(updated, false);   // Keep it off, Esc or closing
        Assert.False(ActiveCheck.Allowed(kept) || ActiveCheck.Asks(kept));
        var on2 = ActiveCheck.Answered(updated, true);   // Turn it on
        Assert.True(ActiveCheck.Allowed(on2) && !ActiveCheck.Asks(on2));
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(kept, AppStore.Json), AppStore.Json)!;
        Assert.False(ActiveCheck.Asks(reloaded));   // never shown again
    }
}
