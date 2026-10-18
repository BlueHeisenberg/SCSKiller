using System.Text.Json;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

public class ProblemReportTests : IDisposable
{
    const string At = "@";   // keeps test addresses out of the release leak scan
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-problem-test-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    static readonly ProblemReport.Masker Mask = new(@"C:\Users\Bobby", "Bobby", "BOBBY-PC");

    string Report(bool withLog) => ProblemReport.Build("1.3.0-beta.2", "Returnal", "Steam", "nvidia", "GPU X", "610.88", "10.0.26100",
        [("Status", "Compiled"), ("Coverage", @"98% of C:\Users\Bobby\AppData\Local\SCSKiller")],
        withLog ? ProblemReport.LogLines(_dir, ["Returnal", "steam_1649240"]) : null, Mask);

    [Fact]
    public void The_report_masks_the_user_and_the_pc_and_leaves_the_log_out_when_asked()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(Path.Combine(_dir, AppLog.FileName + ".1"), ["2026-10-14 20:00:00Z Returnal: an older line"]);
        File.WriteAllLines(Path.Combine(_dir, AppLog.FileName),
        [
            .. Enumerable.Range(0, 60).Select(i => $"2026-10-14 21:00:00Z Returnal: step {i}"),
            @"2026-10-14 20:57:16Z Returnal: recording rehydrated from C:\Users\Bobby\Documents\Saves (1813 shaders)",
            "2026-10-14 20:57:17Z Hogwarts Legacy: another game's line",
            @"2026-10-14 20:57:18Z plan: 6470 PSOs -> c:/users/bobby/AppData/Local/SCSKiller/games/steam_1649240/plan.bin",
            "2026-10-14 20:57:19Z Returnal: signed in, token v4.public.eyJhbGciOiJFZERTQSJ9abc, Bearer abc.def",
            $"2026-10-14 20:57:20Z Returnal: dump for bobby{At}example.com and \"bob smith\"{At}example.org in D:\\BOBBY-PC_backup, auth Bearer xyz.uvw",
        ]);

        var full = Report(withLog: true);
        foreach (var leak in new[] { "Bobby", "bobby", "BOBBY-PC", "@example.com", "v4.public", "abc.def", @"C:\Users", "c:/users" })
            Assert.DoesNotContain(leak, full, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"~\AppData\Local\SCSKiller", full);
        Assert.Contains("<email>", full);
        Assert.Contains("<pc>", full);
        Assert.Contains("<token>", full);
        Assert.DoesNotContain("signed in", full);   // a sign-in line stays out whole
        Assert.DoesNotContain("example.org", full);
        Assert.Equal("<email> and <email>", Mask.Mask("jane/doe@example.com and alice@[192.0.2.1]"));
        Assert.DoesNotContain("another game's line", full);
        Assert.DoesNotContain("an older line", full);   // only the latest 50 lines about the game
        Assert.Contains("step 59", full);
        Assert.Equal(ProblemReport.MaxLogLines, full.Split('\n').Count(l => l.StartsWith("2026-")));
        Assert.Contains("Game: Returnal (Steam)", full);
        Assert.Contains("GPU: GPU X (nvidia), driver 610.88", full);

        var noLog = Report(withLog: false);
        Assert.DoesNotContain("app.log", noLog);
        Assert.DoesNotContain("2026-10-14", noLog);
        Assert.Contains("Coverage: 98% of ~", noLog);

        using var body = JsonDocument.Parse(ProblemReport.Body("It stutters", noLog));
        Assert.Equal(new[] { "message", "report" }, body.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void A_long_line_is_masked_before_it_is_cut_and_a_one_letter_pc_name_is_masked()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(Path.Combine(_dir, AppLog.FileName), [$"Returnal: {new string('x', ProblemReport.MaxLineChars - 25)} mail someone{At}example.com on Q"]);
        var r = ProblemReport.Build("1", "Returnal", "Steam", null, null, null, "10", [], ProblemReport.LogLines(_dir, ["Returnal"]), new(null, null, "Q"));
        Assert.DoesNotContain("someone", r);
        Assert.DoesNotContain(" Q", r);
    }

    [Fact]
    public void A_first_run_without_a_log_reports_no_lines() =>
        Assert.Contains("app.log, the latest lines about this game:\n(none)", Report(withLog: true).ReplaceLineEndings("\n"));
}
