using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

public class AppLogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-applog-test-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    string[] Lines(string file = AppLog.FileName) => File.ReadAllLines(Path.Combine(_dir, file));

    [Fact]
    public async Task Lines_reach_the_file_in_order_with_a_timestamp()
    {
        var log = new AppLog(_dir);
        for (var i = 0; i < 50; i++) log.Report($"line {i}");
        await log.FlushAsync(TimeSpan.FromSeconds(30));
        var lines = Lines();
        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"line {i}"), lines.Select(l => l[(l.IndexOf("Z ") + 2)..]));
        Assert.All(lines, l => Assert.True(DateTimeOffset.TryParse(l[..l.IndexOf("Z ")], out _), l));
    }

    [Fact]
    public async Task Writers_on_several_threads_lose_no_line()
    {
        var log = new AppLog(_dir);
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => log.Report($"step {i}"));
        await log.FlushAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Enumerable.Range(0, 400).Select(i => $"step {i}").Order(), Lines().Select(l => l[(l.IndexOf("Z ") + 2)..]).Order());
    }

    [Fact]
    public async Task The_log_keeps_one_older_file_at_its_limit()
    {
        var log = new AppLog(_dir, maxBytes: 1000);
        for (var i = 0; i < 100; i++) log.Report($"step {i}");
        await log.FlushAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { AppLog.FileName, AppLog.FileName + ".1" }, Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray());
        Assert.True(new FileInfo(Path.Combine(_dir, AppLog.FileName)).Length < 1000 + 100);
        Assert.EndsWith("step 99", Lines()[^1]);
    }

    [Fact]
    public async Task A_log_that_cant_be_written_never_throws()
    {
        Directory.CreateDirectory(_dir);
        var notADir = Path.Combine(_dir, "file");
        File.WriteAllText(notADir, "");
        var log = new AppLog(notADir);
        log.Report("lost");
        await log.FlushAsync(TimeSpan.FromSeconds(30));
        log.Report("after the flush");
        Assert.Equal("", File.ReadAllText(notADir));
    }

    [Theory]
    [InlineData(@"couldn't save C:\Users\Bob Smith\AppData\Local\SCSKiller\games.json: denied", @"couldn't save ~\AppData\Local\SCSKiller\games.json: denied")]
    [InlineData(@"c:/users/bob smith/AppData/x.log and \\?\C:\Users\Bob Smith\Games\a.exe", @"~/AppData/x.log and ~\Games\a.exe")]
    [InlineData(@"C:\Users\Bob Smithers\x", @"C:\Users\Bob Smithers\x")]
    [InlineData(@"no recording in C:\Users\Bob Smith.", "no recording in ~.")]
    [InlineData(@"C:\Users\Bob Smith.old\x", @"C:\Users\Bob Smith.old\x")]
    [InlineData(@"C:\Users\BOBSMI~1\AppData\Local\Temp\a.tmp", @"~\AppData\Local\Temp\a.tmp")]
    [InlineData("signed in as BSMITH, not bsmithy or xbsmith", "signed in as <user>, not bsmithy or xbsmith")]
    [InlineData(@"D:\Games\bsmith\game.exe", @"D:\Games\<user>\game.exe")]
    public void The_profile_folder_and_user_name_are_masked(string line, string masked)
    {
        var (profile, user) = AppLog.Maskers([@"C:\Users\Bob Smith", @"C:\Users\BOBSMI~1"], "bsmith");
        Assert.Equal(masked, AppLog.Mask(line, profile, user));
    }

    [Fact]
    public void A_user_name_under_three_characters_is_left() => Assert.Null(AppLog.Maskers([@"C:\Users\al"], "al").User);

    [Fact]
    public async Task Lines_are_masked_before_they_reach_the_file()
    {
        var log = new AppLog(_dir, profile: @"C:\Users\bsmith", user: "bsmith");
        log.Report(@"SCSKiller started by bsmith from C:\Users\bsmith\AppData\Local\SCSKiller.App");
        await log.FlushAsync(TimeSpan.FromSeconds(30));
        Assert.EndsWith(@"SCSKiller started by <user> from ~\AppData\Local\SCSKiller.App", Assert.Single(Lines()));
    }
}
