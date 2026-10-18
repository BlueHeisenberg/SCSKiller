using System.Net;
using System.Text;
using System.Text.Json;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// Diagnostics reports from synthetic warm logs, against a fake handler: no network.
public class DiagnosticsTests
{
    const string Content = "0123456789abcdef0123456789abcdef01234567";
    static readonly string K1 = new('1', 40), K2 = new('2', 40);

    [Theory]
    [InlineData("the compile didn't stop cleanly (the driver may be stuck), so it was ended", "stuck", null)]
    [InlineData("scskiller_warm exited without a done event", "no_done_event", null)]
    [InlineData(@"scskiller_warm exited with code -1073741819: C:\Users\Alice Mary Smith\x.log | more", "exit_code", "c0000005")]
    [InlineData("scskiller_warm exited with code 5", "exit_code", "00000005")]
    [InlineData(@"no D3D12 device on \\SERVER\share as alice", "reported", null)]
    [InlineData(null, "reported", null)]
    public void A_failed_warm_is_mapped_to_fixed_values(string? error, string reason, string? hr)
    {
        Assert.Equal(new DiagnosticFailure("warm", reason, hr), DiagnosticFailure.FromWarm(error));
    }

    sealed class Generic<T>() : Exception("generic");

    [Fact]
    public void An_exception_gives_its_stage_code_and_plain_type_name_only()
    {
        Assert.Equal(new DiagnosticFailure("plan", "exception", "80070002", "System.IO.FileNotFoundException"),
            DiagnosticFailure.FromException(QueueStage.Planning, new FileNotFoundException(@"C:\Users\alice\x not found")));
        Assert.Equal(new DiagnosticFailure("index", "exception", "80131509", "System.InvalidOperationException"),
            DiagnosticFailure.FromException(QueueStage.Indexing, new InvalidOperationException("x")));
        Assert.Equal("materialize", DiagnosticFailure.FromException(QueueStage.Materializing, new IOException()).Stage);
        Assert.Equal("warm", DiagnosticFailure.FromException(QueueStage.Warming, new IOException()).Stage);
        Assert.Null(DiagnosticFailure.FromException(QueueStage.Planning, new Generic<int>()).Exception);   // a generic type's name isn't plain
    }

    /// <summary>Property: whatever text a failure or the logs carry, none of it reaches the body, only codes, hashes and
    /// the fixed values.</summary>
    [Fact]
    public void No_free_text_reaches_the_report()
    {
        var rng = new Random(1234);
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 \\/:%._-|\"'";
        string Text() => new([.. Enumerable.Range(0, rng.Next(12, 60)).Select(_ => alphabet[rng.Next(alphabet.Length)])]);
        for (var i = 0; i < 500; i++)
        {
            var secret = "Zq" + Text() + "Zq";   // never a hex code, key or fixed value
            DiagnosticFailure failure = (i % 3) switch
            {
                0 => DiagnosticFailure.FromWarm(secret),
                1 => DiagnosticFailure.FromWarm($"scskiller_warm exited with code {rng.Next()}: {secret}"),
                _ => DiagnosticFailure.FromException(QueueStage.Planning, new IOException(secret)),
            };
            var log = Log().Select(l => l + " " + secret).Append(secret).Append($"warm: rejected 'G' hr=0x80070057 key {new string('a', 40)} {secret}");
            var body = Encoding.UTF8.GetString(Diagnostics.Body(Build(log, failure, crashed: [secret])!));
            Assert.DoesNotContain(secret, body);
            Assert.DoesNotContain("Zq", body);
        }
    }

    static string[] Log(int keys = 2) =>
    [
        "warm: start",
        .. Enumerable.Range(0, keys).Select(i => $"warm: rejected 'G' hr=0x80070057 key {i:x40}"),
        $"warm: rejected 'S' hr=0x887a0005 key {K1}",
        "warm: exception 0xc0000005 replaying item 12, skipped",
        "warm: item 4 failed: replay of 'G' (graphics desc) hr=0x80070057",
        "warm: failed x7: replay of 'G' (graphics desc) hr=0x80070057",
        "warm: failed x2: plan item on 'G' (graphics desc) hr=0x80070057",
        "warm: failed x1: replay of 'S' (pipeline stream) hr=0x887a0005",
        "warm: failed x3: replay of 'G' (graphics desc, 12-byte payload) not created: truncated",
    ];

    static DiagnosticsReport? Build(IEnumerable<string> log, DiagnosticFailure? failure = null, string[]? crashed = null) =>
        DiagnosticsReport.Build(log, failure, crashed, Content, "Unreal 4.26", "amd", "AMD Radeon Test", "32.0.1", "1.3.0");

    [Fact]
    public void The_report_holds_the_codes_per_kind_the_rejected_keys_and_the_exceptions()
    {
        var r = Build(Log(), crashed: [K2.ToUpperInvariant(), "not a key"])!;
        Assert.Equal("completed", r.Outcome);
        Assert.Equal([new("G", "80070057", 9), new("S", "887a0005", 1)], r.Rejections);   // a decode failure isn't the driver's
        Assert.Equal([new(0.ToString("x40"), "G", "80070057"), new(1.ToString("x40"), "G", "80070057"), new(K1, "S", "887a0005")], r.Keys);
        Assert.Equal([K2], r.Crashed);
        Assert.Equal(["c0000005"], r.Exceptions);
        Assert.Null(r.Failure);   // only a failed compile's

        var json = JsonDocument.Parse(Diagnostics.Body(r)).RootElement;
        Assert.Equal(["content_hash", "engine", "vendor", "gpu_model", "driver_version", "app_version", "outcome", "rejections", "keys", "crashed", "exceptions", "failure"],
            json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["kind", "hr", "count"], json.GetProperty("rejections")[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void A_clean_compile_has_nothing_to_report_and_the_keys_are_capped()
    {
        Assert.Null(Build(["warm: start", "warm: done"]));
        var failed = Build([], DiagnosticFailure.FromWarm("scskiller_warm exited with code 5"))!;
        Assert.Equal("failed", failed.Outcome);
        Assert.Equal(["stage", "reason", "hr", "exception"], JsonDocument.Parse(Diagnostics.Body(failed)).RootElement.GetProperty("failure").EnumerateObject().Select(p => p.Name));
        var many = Build(Log(DiagnosticsReport.MaxKeys + 50))!;
        Assert.Equal(DiagnosticsReport.MaxKeys, many.Keys.Length);
        Assert.True(Diagnostics.Body(many).Length < 64 << 10);   // the server's cap

        // a process that ended before its tally: the codes come from its rejection lines
        var cut = Build(Log().Where(l => !l.Contains("failed x")))!;
        Assert.Equal([new("G", "80070057", 2), new("S", "887a0005", 1)], cut.Rejections);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.NoContent, 0, false)]
    [InlineData(true, HttpStatusCode.NoContent, 1, true)]
    [InlineData(true, HttpStatusCode.NotFound, 1, false)]
    [InlineData(true, HttpStatusCode.NotImplemented, 1, false)]
    public async Task It_goes_only_with_the_switch_on_with_no_token_and_a_404_or_501_is_quietly_not_sent(bool on, HttpStatusCode answer, int requests, bool sent)
    {
        var seen = new List<(string Line, string Body, bool Auth)>();
        var fake = new CommunityTests.Fake(r =>
        {
            seen.Add(($"{r.Method} {r.RequestUri}", r.Content!.ReadAsStringAsync().Result, r.Headers.Authorization != null));
            return CommunityTests.Ours(answer);
        });
        var d = new Diagnostics(() => on, new RouteFailover(fake, [new("https://api.test.com/")]));
        var report = Build(Log())!;
        Assert.Equal(sent, await d.SendAsync(report));
        Assert.Equal(requests, seen.Count);
        if (requests > 0)
        {
            Assert.Equal("POST https://api.test.com/v1/diagnostics", seen[0].Line);
            Assert.False(seen[0].Auth);
            Assert.Equal(Encoding.UTF8.GetString(Diagnostics.Body(report)), seen[0].Body);
        }
    }

    // The launch offer: the welcome on a first launch, else one question for a user who closed the welcome before it offered this.
    static AppStore TempStore() => new(Path.Combine(Path.GetTempPath(), "scskiller-diag-ask-" + Guid.NewGuid().ToString("N")[..8]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_fresh_install_answers_in_the_welcome_and_is_never_asked_on_its_own(bool share)
    {
        var store = TempStore();
        try
        {
            var first = store.LoadSettings();   // no settings.json yet
            Assert.False(first.WelcomeSeen);    // the welcome, with its diagnostics checkbox
            Assert.False(first.ShareDiagnostics);
            Assert.False(Diagnostics.Asks(first));
            store.SaveSettings(Diagnostics.Answered(first, share) with { WelcomeSeen = true });   // as the welcome closes
            var next = store.LoadSettings();
            Assert.Equal(share, next.ShareDiagnostics);
            Assert.False(Diagnostics.Asks(next));
            Assert.False(Diagnostics.Asks(next with { ShareDiagnostics = false }));   // turned off in Settings later
        }
        finally { Directory.Delete(store.DataDir, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_settings_file_from_before_the_question_is_asked_until_answered_then_never_again(bool share)
    {
        var store = TempStore();
        var file = Path.Combine(store.DataDir, "settings.json");
        try
        {
            store.SaveSettings(AppStore.DefaultSettings with { WelcomeSeen = true });
            File.WriteAllText(file, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), @",\s*""(DiagnosticsAsked|ShareDiagnostics)"": false", ""));
            Assert.DoesNotContain("DiagnosticsAsked", File.ReadAllText(file));
            var old = store.LoadSettings();
            Assert.True(Diagnostics.Asks(old));
            Assert.True(Diagnostics.Asks(store.LoadSettings()));   // a start that couldn't show it asks at the next one

            store.SaveSettings(Diagnostics.Answered(old, share));
            var answered = store.LoadSettings();
            Assert.Equal(share, answered.ShareDiagnostics);
            Assert.False(Diagnostics.Asks(answered));
            Assert.False(Diagnostics.Asks(answered with { ShareDiagnostics = !share }));   // Settings changes it, the question stays answered

            Assert.False(Diagnostics.Asks(old with { ShareDiagnostics = true }));   // already turned on in Settings: nothing to ask
        }
        finally { Directory.Delete(store.DataDir, true); }
    }
}
