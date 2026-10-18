using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// The update system's pure parts (Core/App/Updates.cs). The keys here are generated per run: a test key never exists
// outside this file, and production pins only FeedTrust.ReleaseKeys.
public class UpdateTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-update-test-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly (string Seed, string Public) A = FeedTrust.NewKey(), B = FeedTrust.NewKey();
    static readonly byte[] Feed = """{"Assets":[{"PackageId":"SCSKiller.App","Version":"1.5.0-beta.1","Type":"Full","FileName":"SCSKiller.App-1.5.0-beta.1-full.nupkg","SHA256":"ab"}]}"""u8.ToArray();
    static readonly DateTimeOffset T = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    FeedTrust Trust() => new(new AppStore(_dir), new Dictionary<string, string> { ["rel-a"] = A.Public, ["rel-b"] = B.Public });
    static byte[] Sig(string seed, string kid, string channel, byte[] feed, DateTimeOffset at) =>
        Encoding.UTF8.GetBytes(FeedTrust.Sign(Convert.FromBase64String(seed), kid, channel, feed, at));

    /// <summary>Failed downloads back off 1 h, 2 h, 4 h, 12 h from the failure; a click skips the wait; 8 edge requests in 24 h stop everything.</summary>
    [Fact]
    public void A_failing_package_download_backs_off_and_never_passes_the_daily_cap()
    {
        var t = new PackageTries { At = [T], Failures = 1, FailedAt = T.AddMinutes(30) };
        Assert.Equal(T.AddMinutes(90), UpdateFeeds.PackageHold(t, T.AddMinutes(31), false, true));
        Assert.Null(UpdateFeeds.PackageHold(t, T.AddMinutes(90), false, true));
        Assert.Null(UpdateFeeds.PackageHold(t, T.AddMinutes(31), true, true));
        foreach (var (failures, hours) in new[] { (2, 2), (3, 4), (4, 12), (9, 12) })
            Assert.Equal(T.AddHours(hours), UpdateFeeds.PackageHold(new() { Failures = failures, FailedAt = T }, T.AddSeconds(1), false, true));
        Assert.Equal(T.AddHours(1), UpdateFeeds.PackageHold(new() { Failures = 1, FailedAt = T }, T.AddSeconds(1), false, false));   // GitHub's too
        Assert.Null(UpdateFeeds.PackageHold(new(), T, false, true));

        var day = new PackageTries { At = [.. Enumerable.Range(0, UpdateFeeds.PackagesPerDay).Select(i => T.AddHours(i))] };
        Assert.Equal(T.AddDays(1), UpdateFeeds.PackageHold(day, T.AddHours(9), true, true));   // a click doesn't pass the cap
        Assert.Null(UpdateFeeds.PackageHold(day, T.AddDays(1), true, true));
        Assert.Null(UpdateFeeds.PackageHold(day, T.AddHours(9), true, false));   // GitHub has no quota
        // a clock set back: times ahead of it don't hold anything back (else a year back would hold for a year)
        Assert.Null(UpdateFeeds.PackageHold(day, T.AddHours(-9), true, true));
        Assert.Null(UpdateFeeds.PackageHold(new() { Failures = 4, FailedAt = T }, T.AddHours(-1), false, true));

        new AppStore(_dir).SavePackageTries(day);
        Assert.Equal(day.At, new AppStore(_dir).LoadPackageTries().At);
        File.WriteAllText(Path.Combine(_dir, "package-tries.json"), """{ "At": null, "Failures": 3 }""");   // never a check that throws
        Assert.Null(UpdateFeeds.PackageHold(new AppStore(_dir).LoadPackageTries(), T, false, true));
    }

    /// <summary>A failure, another one, a success, then a failure: the wait starts over at 1 h. A request drops the ones
    /// older than a day.</summary>
    [Fact]
    public void The_backoff_starts_over_after_a_successful_download()
    {
        var t = new PackageTries();
        t.Failed(T);
        Assert.Equal(T.AddHours(1), UpdateFeeds.PackageHold(t, T, false, true));
        t.Failed(T.AddHours(1));
        Assert.Equal(T.AddHours(3), UpdateFeeds.PackageHold(t, T.AddHours(1), false, true));
        t.Succeeded();
        Assert.Null(UpdateFeeds.PackageHold(t, T.AddHours(1), false, true));
        t.Failed(T.AddHours(5));
        Assert.Equal(T.AddHours(6), UpdateFeeds.PackageHold(t, T.AddHours(5), false, true));

        t.At = [T.AddDays(-2), T.AddHours(-1)];
        t.Requested(T);
        Assert.Equal([T.AddHours(-1), T], t.At);
    }

    /// <summary>A Velopack hook's step that throws is logged with the hook, and the steps after it still run.</summary>
    [Fact]
    public void A_failing_hook_step_is_logged_and_the_others_still_run()
    {
        var (log, ran) = (new List<string>(), new List<int>());
        HookSteps.Run("Uninstalling", log.Add, () => ran.Add(1), () => throw new UnauthorizedAccessException("no task scheduler"), () => ran.Add(3));
        Assert.Equal([1, 3], ran);
        Assert.Equal("Uninstalling", log[0]);
        Assert.StartsWith("Uninstalling: step 2 of 3 failed, the others ran: System.UnauthorizedAccessException: no task scheduler", log[1]);
        Assert.Equal(2, log.Count);
    }

    /// <summary>updates.log stays small: at its limit it becomes updates.log.1, the one before goes, the newest lines stay.</summary>
    [Fact]
    public void A_capped_log_keeps_one_older_file()
    {
        for (var i = 0; i < 100; i++) ScsKiller.AppendLog(_dir, "updates.log", $"step {i}", 1000);
        Assert.Equal(new[] { "updates.log", "updates.log.1" }, Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)).Order().ToArray());
        var (log, older) = (new FileInfo(Path.Combine(_dir, "updates.log")), new FileInfo(Path.Combine(_dir, "updates.log.1")));
        Assert.True(log.Length < 1000 + 100 && older.Length >= 1000, $"{log.Length}, {older.Length}");
        Assert.EndsWith("step 99", File.ReadAllLines(log.FullName)[^1]);
        Assert.EndsWith($"step {int.Parse(File.ReadAllLines(log.FullName)[0].Split(' ')[^1]) - 1}", File.ReadAllLines(older.FullName)[^1]);   // no line lost in between
    }

    /// <summary>Writers at once (the app, the CLI, the uninstall hook take the same named lock): a rotation never replaces the
    /// older file with a fresh one another writer had just started.</summary>
    [Fact]
    public void Writers_at_once_keep_a_whole_older_file()
    {
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => ScsKiller.AppendLog(_dir, "updates.log", $"step {i}", 1000));
        var (log, older) = (new FileInfo(Path.Combine(_dir, "updates.log")), new FileInfo(Path.Combine(_dir, "updates.log.1")));
        Assert.True(older.Length >= 1000 && log.Length < 1000 + 100, $"{log.Length}, {older.Length}");
    }

    /// <summary>Without the log's lock (its name taken by another kind of object, or held by another writer) the line is still
    /// appended, unrotated, and nothing throws.</summary>
    [Fact]
    public void A_log_whose_lock_is_unavailable_still_appends()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "updates.log");
        File.WriteAllText(path, new string('x', 2000));
        using (new EventWaitHandle(false, EventResetMode.ManualReset, ScsKiller.LogMutexName(Path.GetFullPath(path))))
            ScsKiller.AppendLog(_dir, "updates.log", "no lock", 1000);
        Assert.EndsWith("no lock", File.ReadAllLines(path)[^1]);

        using var taken = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var m = new Mutex(false, ScsKiller.LogMutexName(Path.GetFullPath(path)));
            m.WaitOne();
            taken.Set();
            done.Wait();
            m.ReleaseMutex();
        });
        holder.Start();
        taken.Wait();
        try { ScsKiller.AppendLog(_dir, "updates.log", "held", 1000); }
        finally { done.Set(); holder.Join(); }
        Assert.EndsWith("held", File.ReadAllLines(path)[^1]);
        Assert.False(File.Exists(path + ".1"));   // never rotated without the lock
    }

    [Fact]
    public void GoodSignature_WithEitherPinnedKey_IsAccepted()
    {
        var t = Trust();
        t.Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T));
        t.Accept("beta", Feed, Sig(B.Seed, "rel-b", "beta", Feed, T.AddHours(1)));   // the backup key
        t.Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T.AddHours(1)));   // the same feed fetched again
    }

    [Fact]
    public void BadSignatures_AreRefused()
    {
        var t = Trust();
        var other = FeedTrust.NewKey();
        Refused(() => t.Accept("beta", Feed, Sig(other.Seed, "rel-a", "beta", Feed, T)), "bad signature");        // not the pinned key
        Refused(() => t.Accept("beta", Feed, Sig(other.Seed, "rel-z", "beta", Feed, T)), "unknown key");          // an unpinned kid
        Refused(() => t.Accept("stable", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T)), "another channel");        // a beta feed served as stable
        Refused(() => t.Accept("beta", Feed, "not json"u8.ToArray()), "malformed");
        Refused(() => new FeedTrust(new AppStore(_dir), FeedTrust.ReleaseKeys).Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T)), "bad signature");   // a test key under a production kid
    }

    [Fact]
    public void TamperedFeedOrSignatureFile_IsRefused()
    {
        var t = Trust();
        var sig = Sig(A.Seed, "rel-a", "beta", Feed, T);
        var feed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Feed).Replace("\"ab\"", "\"cd\""));   // another package hash
        Refused(() => t.Accept("beta", feed, sig), "doesn't match");
        foreach (var (field, value) in new[] { ("signed_at", "2027-01-01T00:00:00Z"), ("feed_sha256", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(feed))) })
        {
            var j = JsonNode.Parse(sig)!;
            j[field] = value;   // signed fields changed after signing
            Refused(() => t.Accept("beta", field == "feed_sha256" ? feed : Feed, Encoding.UTF8.GetBytes(j.ToJsonString())), "bad signature");
        }
    }

    [Fact]
    public void OlderFeed_IsARefusedReplay_PerChannel()
    {
        var t = Trust();
        t.Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T));
        Refused(() => t.Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T.AddMinutes(-1))), "older");
        t.Accept("stable", Feed, Sig(A.Seed, "rel-a", "stable", Feed, T.AddDays(-30)));   // switching back to stable isn't a replay
        Refused(() => Trust().Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T.AddMinutes(-1))), "older");   // remembered on disk
    }

    static void Refused(Action a, string why) => Assert.Contains(why, Assert.Throws<FeedRejectedException>(a).Message);

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "", "stable")]
    [InlineData("v1.5.0-beta.2", 1, 5, 0, "beta.2", "beta")]
    [InlineData("1.5.0-alpha.1", 1, 5, 0, "alpha.1", "alpha")]
    [InlineData("1.5.0-internal.3+abc123", 1, 5, 0, "internal.3", "internal")]
    [InlineData("0.0.0-internal.0+4f2a9c1d", 0, 0, 0, "internal.0", "internal")]   // a dev build
    [InlineData("2.0.0-rc.1", 2, 0, 0, "rc.1", "internal")]                          // an unknown label goes nowhere public
    public void Versions_Parse(string s, int major, int minor, int patch, string pre, string channel)
    {
        var v = AppVersion.Parse(s)!;
        Assert.Equal((major, minor, patch, pre, channel), (v.Major, v.Minor, v.Patch, v.Pre, v.Channel));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("")]
    [InlineData(null)]
    public void NotVersions(string? s) => Assert.Null(AppVersion.Parse(s));

    [Fact]
    public void Versions_OrderBySemVerPrecedence()
    {
        string[] ordered = ["1.4.9", "1.5.0-alpha.2", "1.5.0-alpha.10", "1.5.0-beta", "1.5.0-beta.1", "1.5.0-beta.2", "1.5.0-internal.1", "1.5.0-internal.2",
            "1.5.0-internal.3", "1.5.0-internal.10", "1.5.0", "1.5.1-alpha.1", "1.10.0"];
        var parsed = ordered.Select(s => AppVersion.Parse(s)!).ToList();
        Assert.Equal(ordered, parsed.OrderBy(v => v).Select(v => v.ToString()));
        for (var i = 1; i < parsed.Count; i++) Assert.True(parsed[i - 1].CompareTo(parsed[i]) < 0, $"{parsed[i - 1]} < {parsed[i]}");
        Assert.Equal(0, AppVersion.Parse("1.5.0+a")!.CompareTo(AppVersion.Parse("1.5.0+b")));   // build metadata doesn't order
        Assert.True(AppVersion.Parse("1.3.0-internal.20000000000000000000")!.CompareTo(AppVersion.Parse("1.3.0-internal.100000000000000000000")) < 0);   // past ulong
        Assert.True(AppVersion.Parse("1.5.0-internal.1")!.CompareTo(AppVersion.Parse("1.5.0-beta.3")) > 0);   // internal sorts after beta
    }

    /// <summary>The internal feed still listed internal.2 while internal.3 ran: an older version never installs, nor stays
    /// staged, whatever channel's feed names it.</summary>
    [Fact]
    public void An_older_feed_entry_never_installs_over_a_newer_build()
    {
        var running = AppVersion.Parse("1.3.0-internal.3")!;
        foreach (var (channel, older) in new[] { ("internal", "1.3.0-internal.2"), ("internal", "1.3.0-alpha.9"), ("internal", "1.2.4"), ("alpha", "1.3.0-alpha.9"), ("beta", "1.3.0-beta.1"), ("stable", "1.2.4") })
        {
            var s = Staged with { Channel = channel, Version = older };
            Assert.Equal(StartStep.Older, s.AtStart(running, handedOver: false));
            Assert.NotEqual(StartStep.Apply, s.AtStart(running, handedOver: true));
            Assert.False(AutoInstall.OffersRestart(s, channel, running, [channel]), $"{older} from {channel} over {running}");
            Assert.False(AutoInstall.OffersRestart(s, channel, running, null));
        }
        // the same version is installed, not offered again
        Assert.False(AutoInstall.OffersRestart(Staged with { Channel = "internal", Version = "1.3.0-internal.3" }, "internal", running, ["internal"]));
        var newer = Staged with { Channel = "internal", Version = "1.3.0-internal.4" };
        Assert.Equal(StartStep.Apply, newer.AtStart(running, false));
        Assert.True(AutoInstall.OffersRestart(newer, "internal", running, ["internal"]));
        Assert.True(AutoInstall.OffersRestart(Staged with { Channel = "internal", Version = "1.3.0" }, "internal", running, ["internal"]));   // the release passes its pre-releases
        Assert.False(AutoInstall.OffersRestart(Staged with { Channel = "internal", Version = "1.3.0-internal.3" }, "internal", AppVersion.Parse("1.3.0")!, ["internal"]));

        // "Go back to stable now": only the stable feed's download from that click, never a record read back at a start
        var back = Staged with { Channel = "stable", Version = "1.2.4" };
        Assert.True(AutoInstall.OffersRestart(back, "stable", running, ["internal"], backToStable: true));
        Assert.False(AutoInstall.OffersRestart(back, "stable", running, ["internal"]));
        Assert.False(AutoInstall.OffersRestart(back with { Channel = "internal" }, "internal", running, ["internal"], backToStable: true));
        Assert.Equal(StartStep.Older, back.AtStart(running, false));

        // Velopack picks and stages from the feed: it takes a downgrade only for that click
        var updater = File.ReadAllText(Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.App", "Updater.cs"));
        Assert.Contains("ExplicitChannel = channel, AllowVersionDowngrade = downgrade,", updater);
        Assert.Contains("static Download? Usable(Download? d) => AutoInstall.OffersRestart(d?.S, App.Core.Settings.UpdateChannel, AppVersion.Current, Ent(), d?.BackToStable == true) ? d : null;", updater);
        Assert.Contains("SignedFeedSource.Fetched), backToStable));", updater);
        Assert.Matches(@"return new\(m, new VelopackAsset\s*\{[^}]*\}, s\);", updater);   // a record from the disk is never back-to-stable's
    }

    [Fact]
    public void The_internal_channel_offers_updates_but_never_installs_them_by_itself()
    {
        var on = AppStore.DefaultSettings;
        Assert.True(on.InstallUpdatesAutomatically);
        Assert.Equal(AutoInstall.Internal, AutoInstall.HeldBack(on, "internal", compiling: false, offline: false, playing: false));
        Assert.False(AutoInstall.On(on, "internal"));
        Assert.All(new[] { "stable", "beta", "alpha" }, c => Assert.True(AutoInstall.On(on, c)));
        var staged = Staged with { Channel = "internal", Version = "1.3.0-internal.4" };
        Assert.True(AutoInstall.OffersRestart(staged, "internal", AppVersion.Parse("1.3.0-internal.3")!, ["internal"]));
        Assert.Contains("Restart to update", AutoInstall.ReadyNote("1.3.0-internal.4", AutoInstall.On(on, "internal")));
        Assert.DoesNotContain("starts or quits", AutoInstall.ReadyNote("1.3.0-internal.4", AutoInstall.On(on, "internal")));
        var about = Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.App", "Pages", "AboutPage.xaml");   // its checkbox does nothing there
        Assert.Contains("Visibility=\"{x:Bind Vm.ShowsAutoInstall, Mode=OneWay}\"", File.ReadAllText(about));
        Assert.Contains("ShowsAutoInstall => ShowsCheck && Updater.Channel != UpdateChannels.Internal;", File.ReadAllText(about + ".cs"));
    }

    [Fact]
    public void Channels_AreGatedByTheTokensFlags()
    {
        Assert.Equal(["stable"], UpdateChannels.Offered(null));                          // signed out: no picker
        Assert.Equal(["stable"], UpdateChannels.Offered(["db"]));
        Assert.Equal(["stable", "beta"], UpdateChannels.Offered(["db", "beta"]));
        Assert.Equal(["stable", "beta", "alpha"], UpdateChannels.Offered(["db", "beta", "alpha", "prio"]));
        Assert.Equal(["stable", "beta", "alpha", "internal"], UpdateChannels.Offered(["beta", "alpha", "internal"]));

        Assert.Equal("beta", UpdateChannels.Effective("beta", "stable", ["beta"]));
        Assert.Equal("alpha", UpdateChannels.Effective(null, "alpha", ["beta", "alpha"]));    // null: the build's own channel
        Assert.Equal("beta", UpdateChannels.Effective("alpha", "alpha", ["beta"]));           // alpha lapsed: beta, not stable
        Assert.Equal("stable", UpdateChannels.Effective("beta", "beta", ["db"]));             // beta lapsed
        Assert.Equal("stable", UpdateChannels.Effective("beta", "beta", null));               // signed out
        Assert.Equal("stable", UpdateChannels.Effective("stable", "beta", ["beta"]));         // chose stable: waits for stable to pass the beta
        Assert.Equal("stable", UpdateChannels.Effective(null, "stable", ["beta", "alpha"]));
        Assert.Equal("stable", UpdateChannels.Effective("nonsense", "beta", ["beta"]));
    }

    [Fact]
    public void A_staged_download_installs_only_while_its_channel_is_chosen()
    {
        Assert.True(UpdateChannels.Installs("internal", "internal", "internal", ["internal"]));
        Assert.False(UpdateChannels.Installs("internal", "internal", "internal", ["db"]));      // lapsed: the token says so
        Assert.True(UpdateChannels.Installs("stable", "beta", "stable", []));                   // signed out: stable
        Assert.False(UpdateChannels.Installs("beta", "beta", "stable", []));
        // signed in, no token read yet (a logon without network): any channel up to the chosen one
        Assert.True(UpdateChannels.Installs("internal", "internal", "internal", null));
        Assert.True(UpdateChannels.Installs("stable", "internal", "internal", null));
        Assert.True(UpdateChannels.Installs("stable", null, "beta", null));
        Assert.False(UpdateChannels.Installs("alpha", "beta", "beta", null));                   // switched to an earlier channel
        Assert.False(UpdateChannels.Installs("internal", "stable", "internal", null));
    }

    static readonly byte[] StableFeed = """{"Assets":[{"PackageId":"SCSKiller.App","Version":"1.2.4","Type":"Full","FileName":"SCSKiller.App-1.2.4-stable-full.nupkg","SHA256":"AB12","Size":124642678}]}"""u8.ToArray();
    static readonly byte[] BetaFeed = """{"Assets":[{"PackageId":"SCSKiller.App","Version":"1.3.0-beta.1","Type":"Full","FileName":"SCSKiller.App-1.3.0-beta.1-beta-full.nupkg","SHA256":"CD34","Size":124000000}]}"""u8.ToArray();
    static readonly StagedUpdate Staged = new("stable", "1.2.4", "SCSKiller.App-1.2.4-stable-full.nupkg", 124_642_678, "ab12",
        new Dictionary<string, SignedFeed> { ["stable"] = new(StableFeed, Sig(A.Seed, "rel-a", "stable", StableFeed, T)) });
    static readonly StagedUpdate StagedBeta = new("beta", "1.3.0-beta.1", "SCSKiller.App-1.3.0-beta.1-beta-full.nupkg", 124_000_000, "cd34",
        new Dictionary<string, SignedFeed> { ["beta"] = new(BetaFeed, Sig(A.Seed, "rel-a", "beta", BetaFeed, T)), ["stable"] = Staged.Feeds["stable"] });
    static readonly Dictionary<string, string> Keys = new() { ["rel-a"] = A.Public };

    [Fact]
    public void A_staged_download_survives_the_restart()
    {
        Assert.Null(StagedUpdate.Load(_dir));
        Staged.Save(_dir);
        var loaded = StagedUpdate.Load(_dir)!;
        Assert.Equal((Staged.Channel, Staged.Version, Staged.FileName, Staged.Size, Staged.Sha256), (loaded.Channel, loaded.Version, loaded.FileName, loaded.Size, loaded.Sha256));
        Assert.Null(loaded.Refused(Keys));   // the feed and its signature, byte for byte
        File.WriteAllText(Path.Combine(_dir, "update-staged.json"), "{\"Channel\":\"stable\"");   // torn write
        Assert.Null(StagedUpdate.Load(_dir));
        File.WriteAllText(Path.Combine(_dir, "update-staged.json"), "{}");
        Assert.Null(StagedUpdate.Load(_dir));
        Staged.Save(_dir);
        StagedUpdate.Forget(_dir);
        Assert.Null(StagedUpdate.Load(_dir));
        StagedUpdate.Forget(_dir);   // nothing to forget
    }

    [Fact]
    public void A_staged_record_its_signed_feeds_dont_back_is_refused()
    {
        Assert.Null(Staged.Refused(Keys));
        Assert.Null(StagedBeta.Refused(Keys));
        Assert.Null((StagedBeta with { Channel = "internal" }).Refused(Keys));   // internal's check reads beta's and stable's feeds
        // the record says stable but holds only a beta feed: a beta package passed off as stable
        Assert.NotNull((Staged with { Version = StagedBeta.Version, FileName = StagedBeta.FileName, Size = StagedBeta.Size, Sha256 = StagedBeta.Sha256,
            Feeds = new Dictionary<string, SignedFeed> { ["beta"] = StagedBeta.Feeds["beta"] } }).Refused(Keys));
        Assert.NotNull((StagedBeta with { Channel = "stable" }).Refused(Keys));
        Assert.NotNull((Staged with { Sha256 = "ef56" }).Refused(Keys));      // another package's hash
        Assert.NotNull((Staged with { Size = 1 }).Refused(Keys));
        Assert.NotNull((Staged with { Version = "1.9.0" }).Refused(Keys));
        Assert.NotNull((Staged with { FileName = "other.nupkg" }).Refused(Keys));
        Assert.NotNull((Staged with { Channel = "nonsense" }).Refused(Keys));
        Assert.NotNull((Staged with { Feeds = new Dictionary<string, SignedFeed>() }).Refused(Keys));
        var edited = StableFeed.ToArray();
        edited[^5] = (byte)'9';   // the size in the feed, after it was signed
        Assert.NotNull((Staged with { Feeds = new Dictionary<string, SignedFeed> { ["stable"] = Staged.Feeds["stable"] with { Feed = edited } } }).Refused(Keys));
        Assert.NotNull((Staged with { Feeds = new Dictionary<string, SignedFeed> { ["stable"] = new(BetaFeed, StagedBeta.Feeds["beta"].Sig) } }).Refused(Keys));   // beta's signature under stable
        Assert.NotNull(Staged.Refused(new Dictionary<string, string> { ["rel-b"] = B.Public }));   // a key that isn't pinned
    }

    [Fact]
    public void The_start_applies_a_staged_update()
    {
        Staged.Save(_dir);
        var staged = StagedUpdate.Load(_dir)!;
        Assert.Equal(StartStep.Apply, staged.AtStart(AppVersion.Parse("1.2.3")!, handedOver: false));
        Assert.Null(AutoInstall.HeldBack(AppStore.DefaultSettings, "stable", compiling: false, offline: false, playing: false));
        Assert.Equal(StartStep.Installed, staged.AtStart(AppVersion.Parse("1.2.4")!, handedOver: true));   // the hook didn't clear the marker
        Assert.Equal(StartStep.Failed, staged.AtStart(AppVersion.Parse("1.2.3")!, handedOver: true));      // never a restart loop
        Assert.Equal(StartStep.Older, staged.AtStart(AppVersion.Parse("1.3.0")!, handedOver: false));
        Assert.Equal(StartStep.Older, (staged with { Channel = "internal", Version = "1.2.4-internal.1" }).AtStart(AppVersion.Parse("1.2.4")!, false));
    }

    [Fact]
    public void Automatic_install_is_on_by_default_also_for_an_older_settings_file()
    {
        Assert.True(AppStore.DefaultSettings.InstallUpdatesAutomatically);
        var store = new AppStore(_dir);
        store.SaveSettings(AppStore.DefaultSettings with { StartWithWindows = false });
        var file = Path.Combine(_dir, "settings.json");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.True(json.Remove("InstallUpdatesAutomatically"));
        File.WriteAllText(file, json.ToJsonString());
        Assert.True(store.LoadSettings().InstallUpdatesAutomatically);
        Assert.False(store.LoadSettings().StartWithWindows);
    }

    [Fact]
    public void Nothing_installs_by_itself_with_the_setting_off()
    {
        var off = AppStore.DefaultSettings with { InstallUpdatesAutomatically = false };
        Assert.Equal(AutoInstall.Off, AutoInstall.HeldBack(off, "stable", false, false, false));
        Assert.False(AutoInstall.On(off, "stable"));
        Assert.DoesNotContain("starts or quits", AutoInstall.ReadyNote("1.2.4", AutoInstall.On(off, "stable")));
    }

    [Fact]
    public void A_compile_an_offline_session_or_a_game_holds_the_install_back()
    {
        var on = AppStore.DefaultSettings;
        Assert.Equal(AutoInstall.Compiling, AutoInstall.HeldBack(on, "stable", compiling: true, offline: false, playing: false));
        Assert.Equal(AutoInstall.Offline, AutoInstall.HeldBack(on, "stable", compiling: false, offline: true, playing: false));
        Assert.Equal(AutoInstall.Playing, AutoInstall.HeldBack(on, "stable", compiling: false, offline: false, playing: true));
    }

    [Fact]
    public void Restart_to_update_is_offered_for_a_staged_update_with_automatic_install_on_or_off()
    {
        foreach (var s in new[] { AppStore.DefaultSettings, AppStore.DefaultSettings with { InstallUpdatesAutomatically = false } })
        {
            Assert.True(AutoInstall.OffersRestart(Staged, s.UpdateChannel, AppVersion.Parse("1.2.3")!, []));
            Assert.Contains("Restart to update", AutoInstall.ReadyNote("1.2.4", AutoInstall.On(s, "stable")));
        }
        Assert.Contains("starts or quits", AutoInstall.ReadyNote("1.2.4", AutoInstall.On(AppStore.DefaultSettings, "stable")));
        Assert.False(AutoInstall.OffersRestart(null, null, AppVersion.Parse("1.2.3")!, []));
    }

    [Fact]
    public void BusyMutex_TestHoldsNeverShowUnderTheAppsNames()
    {
        const string busy = @"Local\SCSKiller.Busy", deleting = @"Local\SCSKiller.CacheDelete";
        Assert.NotEqual(busy, Busy.Name);
        Assert.NotEqual(deleting, ScsKiller.DeletingName);
        Assert.Equal(Busy.Scope, Busy.ScopeFrom(Busy.Scope));
        Assert.All([null, "", @".test-\x", ".test-" + new string('0', 31) + @"\", "x" + Busy.Scope[1..]], v => Assert.Equal("", Busy.ScopeFrom(v)));
        bool busyBefore = Busy.IsHeld(busy), deletingBefore = Busy.IsHeld(deleting), busyDuring, deletingDuring;
        using (Busy.Hold())
        using (Busy.Hold(ScsKiller.DeletingName))
        {
            Assert.True(Busy.IsHeld());
            Assert.True(Busy.IsHeld(ScsKiller.DeletingName));
            (busyDuring, deletingDuring) = (Busy.IsHeld(busy), Busy.IsHeld(deleting));
        }
        // a name that came and went with this hold alone: an app in this session holds it for longer than the check
        Assert.False(!busyBefore && busyDuring && !Busy.IsHeld(busy), "a test hold shows as " + busy);
        Assert.False(!deletingBefore && deletingDuring && !Busy.IsHeld(deleting), "a test hold shows as " + deleting);
    }

    [Fact]
    public async Task BusyMutex_GuardsTheApply()
    {
        var name = @"Local\SCSKiller.Busy.test-" + Guid.NewGuid().ToString("N");   // not the real one: queue tests may hold it
        Assert.False(Busy.IsHeld(name));
        var h1 = Busy.Hold(name);
        var h2 = await Task.Run(() => Busy.Hold(name));   // another thread (or process) holding it too
        Assert.True(Busy.IsHeld(name));
        h1.Dispose();
        Assert.True(Busy.IsHeld(name));
        await Task.Run(h2.Dispose);   // released from a thread that didn't create it: no ownership needed
        Assert.False(Busy.IsHeld(name));
    }

    /// <summary>The worker's side of the handshake: it holds Busy before it reads the marker, and lets go while an update
    /// is handed over; the updater marks first, then reads Busy. Either order of the two sees the other.</summary>
    [Fact]
    public void A_compile_worker_holds_first_then_backs_off_while_an_update_is_handed_over()
    {
        var name = @"Local\SCSKiller.Busy.test-" + Guid.NewGuid().ToString("N");
        using (var hold = Busy.TryHold(_dir, T, name))
        {
            Assert.NotNull(hold);
            Busy.MarkApplying(_dir, T);    // the updater, after the worker's hold
            Assert.True(Busy.IsHeld(name));   // ...sees the compile, and doesn't apply
            Busy.ClearApplying(_dir);
        }
        Busy.MarkApplying(_dir, T);        // the updater first
        Assert.Null(Busy.TryHold(_dir, T, name));   // the worker doesn't start
        Assert.False(Busy.IsHeld(name));   // ...and holds nothing, so the apply goes ahead
        using (var late = Busy.TryHold(_dir, T.AddMinutes(3), name)) Assert.NotNull(late);   // a crashed apply's marker no longer stops it
        Busy.ClearApplying(_dir);
    }

    [Fact]
    public void ApplyingMarker_StopsTheCliForTwoMinutes()
    {
        Assert.False(Busy.Applying(_dir, T));
        Busy.MarkApplying(_dir, T);
        Assert.True(Busy.Applying(_dir, T.AddSeconds(90)));
        Assert.False(Busy.Applying(_dir, T.AddMinutes(3)));    // a crashed apply
        Assert.False(Busy.Applying(_dir, T.AddMinutes(-3)));   // the clock went back: not forever
        Busy.ClearApplying(_dir);
        Assert.False(Busy.Applying(_dir, T));
        Busy.ClearApplying(_dir);   // already gone
    }

    [Fact]
    public void Codecs_OnlyPinnedBytesAreKept()
    {
        string dir = Path.Combine(_dir, "codecs"), seed = Path.Combine(_dir, "seed");
        Directory.CreateDirectory(seed);
        File.WriteAllText(Path.Combine(seed, "zlib-ng2.dll"), "not it");   // a wrong copy next to the exe isn't used
        var downloads = 0;
        var e = Assert.Throws<InvalidDataException>(() => Codecs.Ensure("zlib-ng2.dll", p => { downloads++; File.WriteAllText(p, "tampered"); return true; }, dir, seed));
        Assert.Contains("SHA-256", e.Message);
        Assert.Equal(1, downloads);
        Assert.False(File.Exists(Path.Combine(dir, "zlib-ng2.dll")));   // never left behind to load
        Assert.Throws<InvalidDataException>(() => Codecs.Ensure("other.dll", _ => true, dir, seed));   // no pin, no load
    }

    /// <summary>Every reader loads the codecs before reading a game: a codec that can't be had must leave it to the managed
    /// decoders, never throw (an Unreal game then fell through to the carver as "engine not supported yet").</summary>
    [Fact]
    public void Codecs_AFailedLoadDoesntThrowIsLoggedOnceAndIsntRetried()
    {
        var name = $"test-{Guid.NewGuid():N}.dll";
        var (lines, downloads, inits) = (new List<string>(), 0, 0);
        var log = new Lines(lines.Add);
        bool Download(string _) { downloads++; throw new IOException("downloading https://example.invalid/codec.zip failed: blocked"); }
        Assert.False(Codecs.TryLoad(name, Download, _ => inits++, log, Path.Combine(_dir, "codecs")));
        Assert.False(Codecs.TryLoad(name, Download, _ => inits++, log, Path.Combine(_dir, "codecs")));
        Assert.Equal((1, 0), (downloads, inits));
        Assert.Contains(name, Assert.Single(lines));
        Assert.Contains("https://example.invalid/codec.zip", lines[0]);

        var dir = Path.Combine(_dir, "pinned");   // a download that isn't the pinned build: never loaded, never left under its name
        Assert.False(Codecs.TryLoad("zlib-ng2.dll", p => { File.WriteAllText(p, "tampered"); return true; }, _ => inits++, log, dir, seedDir: _dir));
        Assert.Equal(0, inits);
        Assert.Contains("SHA-256", lines[1]);
        Assert.Empty(Directory.GetFiles(dir));

        // Oodle data that doesn't decode without the codec: the user sees the short line alone, the details go to the log
        var shown = Codecs.Reason(new AggregateException(new InvalidDataException(Codecs.NoOodleDll, new InvalidDataException("cannot decompress 40 -> 64 bytes"))));
        Assert.Equal(Codecs.NoOodleDll, shown);
        Assert.DoesNotContain(';', shown);
        Assert.DoesNotContain(';', Codecs.NoNativeOodle);
        Assert.Null(Codecs.Reason(new InvalidDataException("cannot decompress 40 -> 64 bytes")));
    }

    sealed class Lines(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }

    [Fact]
    public void SourceCode_AndPackageUrls()
    {
        Assert.Equal($"https://github.com/{UpdateFeeds.GhRepo}/tree/v1.4.2", UpdateFeeds.Source(AppVersion.Parse("1.4.2")!)!.AbsoluteUri);
        Assert.Equal(new Uri(RouteFailover.Default.Primary, "v1/updates/beta/source-1.5.0-beta.2.zip"), UpdateFeeds.Source(AppVersion.Parse("1.5.0-beta.2+abc")!));
        Assert.Equal(new Uri(RouteFailover.Default.Primary, "v1/updates/alpha/source-1.5.0-alpha.1.zip"), UpdateFeeds.Source(AppVersion.Parse("1.5.0-alpha.1")!));
        Assert.Null(UpdateFeeds.Source(AppVersion.Parse("1.5.0-internal.1")!));
        Assert.Null(UpdateFeeds.Source(AppVersion.Parse("0.0.0-internal.0+abc")!));

        Assert.Equal($"https://github.com/{UpdateFeeds.GhRepo}/releases/latest/download/releases.stable.json", UpdateFeeds.Feed("stable", "releases.stable.json").AbsoluteUri);
        Assert.Equal(new Uri(RouteFailover.Default.Primary, "v1/updates/beta/releases.beta.json.sig"), UpdateFeeds.Feed("beta", "releases.beta.json.sig"));
        // a beta feed lists stable releases too: each package is fetched from its own version's channel
        Assert.Equal($"https://github.com/{UpdateFeeds.GhRepo}/releases/download/v1.4.2/SCSKiller.App-1.4.2-full.nupkg",
            UpdateFeeds.Package(AppVersion.Parse("1.4.2")!, "SCSKiller.App-1.4.2-full.nupkg").AbsoluteUri);
        if (Environment.GetEnvironmentVariable("SCSKILLER_API") is not { Length: > 0 })
            Assert.Equal("https://dl.scskiller.io/v1/updates/beta/SCSKiller.App-1.5.0-beta.2-delta.nupkg",
                UpdateFeeds.Package(AppVersion.Parse("1.5.0-beta.2")!, "SCSKiller.App-1.5.0-beta.2-delta.nupkg").AbsoluteUri);
    }

    [Fact]
    public void Checks_RunHourly() => Assert.Equal(TimeSpan.FromHours(1), UpdateFeeds.CheckEvery);

    /// <summary>The App project is WinUI and has no test seam: its source says that "Check for updates" and the Library's
    /// refresh download what they find.</summary>
    [Fact]
    public void ManualCheck_AndLibraryRefresh_Download()
    {
        var app = Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.App");
        var updater = File.ReadAllText(Path.Combine(app, "Updater.cs"));
        Assert.Matches(@"public static async Task CheckAsync\(bool backToStable = false\)", updater);
        var now = System.Text.RegularExpressions.Regex.Match(updater, @"public static Task CheckNowAsync\(\)\s*\{(.*?)\r?\n    \}",System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(now.Success);
        Assert.Contains("return CheckAsync();", now.Groups[1].Value);
        Assert.Contains("_ = Updater.CheckNowAsync();", File.ReadAllText(Path.Combine(app, "Pages", "AboutPage.xaml.cs")));
        Assert.Matches(@"real\.UserFetch = async \(\) => \{ await Account\.RefreshAsync\(\); await Updater\.CheckAsync\(\); \};", File.ReadAllText(Path.Combine(app, "App.xaml.cs")));
    }

    /// <summary>The App's source: the start and the tray's Quit both ask <see cref="AutoInstall.HeldBack"/> before applying,
    /// the start takes the staged download from the disk, and Windows' session end never applies.</summary>
    [Fact]
    public void Start_and_Quit_apply_only_through_the_automatic_install_rules()
    {
        var app = Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.App");
        var updater = File.ReadAllText(Path.Combine(app, "Updater.cs"));
        string Body(string signature) => System.Text.RegularExpressions.Regex.Match(updater, System.Text.RegularExpressions.Regex.Escape(signature) + @"\s*\{(.*?)\r?\n    \}",
            System.Text.RegularExpressions.RegexOptions.Singleline) is { Success: true } m ? m.Groups[1].Value : throw new Xunit.Sdk.XunitException(signature);
        var start = Body("public static async Task ApplyAtStartAsync(string[] args)");
        Assert.Contains("Staged()", start);
        Assert.Contains("case StartStep.Apply: ready = d;", start);
        Assert.Matches(@"HeldBack\(r\.Channel\) is null && BeginUpdate\(\)\s*&& !await ApplyAsync\(TimeSpan\.Zero, \(m, a\) => m\.ApplyUpdatesAndRestart\(a, args\)", start);
        var quit = Body("public static async Task ApplyOnExitAsync()");
        Assert.True(quit.IndexOf("HeldBack(d.Channel)") is >= 0 and var held && held < quit.IndexOf("ApplyAsync("));
        // a game started during Quit's wait for the check is seen right before the handover
        Assert.Contains("m.WaitExitThenApplyUpdates(r, silent: true, restart: false), now => !GameRunning() && AutoInstall.On(App.Core.Settings, now.Channel))", quit);
        Assert.Contains("static bool GameRunning() => App.Core is ScsKiller k ? k.GameRunning()", updater);
        // the start scans whether or not a game is known to run: before its scan, GameRunning() can only say "maybe"
        Assert.Matches(@"Untouched\(\) && AutoInstall\.On\(App\.Core\.Settings, u\.Channel\)\) await App\.Core\.ScanAsync\(", start);
        Assert.Contains("AutoInstall.HeldBack(App.Core.Settings, channel, Busy.IsHeld() || App.Core.Compiling, OfflineBlocks(), GameRunning())", updater);
        // the start knows the games before it asks whether one runs, and asks again right before the handover
        Assert.True(start.IndexOf("await App.Core.ScanAsync(") is >= 0 and var scan && scan < start.IndexOf("BeginUpdate()"));
        Assert.Contains("now => Untouched() && !GameRunning() && AutoInstall.On(App.Core.Settings, now.Channel)", start);   // the download actually handed over
        Assert.Contains("s.Refused(FeedTrust.ReleaseKeys)", updater);
        Assert.Contains(".SetAutoApplyOnStartup(false)", File.ReadAllText(Path.Combine(app, "UpdateHooks.cs")));
        Assert.Contains("if (ready?.Channel == channel && !SignedFeedSource.Partial) Stage(null);", updater);   // a feed missing a channel drops nothing
        var appXaml = File.ReadAllText(Path.Combine(app, "App.xaml.cs"));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(appXaml, @"Updater\.ApplyOnExitAsync\("));   // QuitAsync only, not SessionEnd
    }

    /// <summary>A package host can't fill the disk past the signed feed's size, nor hold the update check forever.</summary>
    [Fact]
    public async Task Download_StopsAtTheFeedsSize_AndOnAStall()
    {
        var to = new MemoryStream();
        await UpdateFeeds.Download(new MemoryStream(new byte[1000]), to, 1000, TimeSpan.FromSeconds(5), default);
        Assert.Equal(1000, to.Length);
        await Assert.ThrowsAsync<FeedRejectedException>(() => UpdateFeeds.Download(new MemoryStream(new byte[1001]), new MemoryStream(), 1000, TimeSpan.FromSeconds(5), default));
        await Assert.ThrowsAsync<TimeoutException>(() => UpdateFeeds.Download(new Stalling(), new MemoryStream(), 1000, TimeSpan.FromMilliseconds(200), default));
    }

    static readonly byte[] InternalFeed = """{"Assets":[{"PackageId":"SCSKiller.App","Version":"1.2.3-internal.5","Type":"Full","FileName":"SCSKiller.App-1.2.3-internal.5-internal-full.nupkg","SHA256":"EF56","Size":4}]}"""u8.ToArray();
    const string InternalPackage = "SCSKiller.App-1.2.3-internal.5-internal-full.nupkg";

    /// <summary>The servers by host and path: 404 for anything not given, 429 (Retry-After an hour) for a limited host.</summary>
    sealed class Servers(Dictionary<string, byte[]> files, params string[] limited) : HttpMessageHandler
    {
        public readonly List<Uri> Asked = [];
        public int To(string host) => Asked.Count(u => u.Host == host);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!;
            lock (Asked) Asked.Add(url);
            if (url.Host != "github.com") Assert.Equal("tok", request.Headers.Authorization?.Parameter);
            if (limited.Contains(url.Host))
            {
                var r = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new(TimeSpan.FromHours(1));
                return Task.FromResult(r);
            }
            var file = files.FirstOrDefault(f => url.AbsolutePath.EndsWith("/" + f.Key)).Value;
            return Task.FromResult(file is null ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) : new HttpResponseMessage { Content = new ByteArrayContent(file) });
        }
    }

    static Dictionary<string, byte[]> Signed(string channel, byte[] feed, string seed = "") => new()
    {
        [$"releases.{channel}.json"] = feed,
        [$"releases.{channel}.json.sig"] = Sig(seed.Length > 0 ? seed : A.Seed, "rel-a", channel, feed, T),
    };

    FeedClient Client(Servers s, List<string>? log = null) => new(s, Trust(), () => Task.FromResult<string?>("tok"), l => log?.Add(l));

    /// <summary>GitHub limiting the stable feed holds back neither the internal feed nor the internal package: the newer
    /// internal build is still read and downloaded, and GitHub isn't asked again before its Retry-After.</summary>
    [Fact]
    public async Task A_rate_limited_stable_feed_doesnt_hold_back_a_newer_internal_package()
    {
        var files = Signed("internal", InternalFeed);
        files[InternalPackage] = [1, 2, 3, 4];
        var s = new Servers(files, "github.com");
        var log = new List<string>();
        var c = Client(s, log);
        for (var i = 0; i < 2; i++)
        {
            var (feeds, partial) = await c.ReadAsync("internal");
            Assert.Equal(["internal"], feeds.Select(f => f.Channel));
            Assert.Equal(InternalFeed, feeds[0].Feed.Feed);
            Assert.True(partial);   // stable left out: a staged download it lists isn't dropped
        }
        Assert.Equal(1, s.To("github.com"));
        const string channel = "internal";
        Assert.Equal(4, s.Asked.Count(u => u.AbsolutePath.EndsWith($"releases.{channel}.json") || u.AbsolutePath.EndsWith($"releases.{channel}.json.sig")));
        Assert.Contains(log, l => l.Contains("github.com"));
        using var package = await c.PackageAsync(AppVersion.Parse("1.2.3-internal.5")!, InternalPackage, default);
        Assert.Equal([1, 2, 3, 4], await package.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_rate_limited_stable_feed_waits_on_the_stable_channel()
    {
        var s = new Servers([], "github.com");
        var c = Client(s);
        for (var i = 0; i < 2; i++)
            Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, (await Assert.ThrowsAsync<HttpRequestException>(() => c.ReadAsync("stable"))).StatusCode);
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => c.PackageAsync(AppVersion.Parse("1.2.4")!, "SCSKiller.App-1.2.4-stable-full.nupkg", default));
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, e.StatusCode);
        Assert.Equal(1, s.To("github.com"));   // the second read and the package waited without asking
    }

    [Fact]
    public async Task A_limited_package_host_doesnt_stop_the_feeds()
    {
        if (Environment.GetEnvironmentVariable("SCSKILLER_API") is { Length: > 0 }) return;   // packages then share the API host
        var s = new Servers(Signed("internal", InternalFeed), "dl.scskiller.io");
        var c = Client(s);
        for (var i = 0; i < 2; i++)
        {
            var e = await Assert.ThrowsAsync<HttpRequestException>(() => c.PackageAsync(AppVersion.Parse("1.2.3-internal.5")!, InternalPackage, default));
            Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, e.StatusCode);
        }
        Assert.Equal(1, s.To("dl.scskiller.io"));
        Assert.Single((await c.ReadAsync("internal")).Feeds);
    }

    [Fact]
    public async Task The_signature_is_still_required_beside_a_rate_limited_feed()
    {
        var other = FeedTrust.NewKey();
        await Assert.ThrowsAsync<FeedRejectedException>(() => Client(new Servers(Signed("internal", InternalFeed, other.Seed), "github.com")).ReadAsync("internal"));
        // a later channel's feed that fails its signature fails the read too
        var files = Signed("internal", InternalFeed);
        foreach (var (k, v) in Signed("stable", StableFeed, other.Seed)) files[k] = v;
        await Assert.ThrowsAsync<FeedRejectedException>(() => Client(new Servers(files)).ReadAsync("internal"));
    }

    /// <summary>beta and alpha unpublished (404) are channels without a feed, not feeds left out.</summary>
    [Fact]
    public async Task Unpublished_channels_are_no_feed_rather_than_a_gap()
    {
        var files = Signed("internal", InternalFeed);
        foreach (var (k, v) in Signed("stable", StableFeed)) files[k] = v;
        var (feeds, partial) = await Client(new Servers(files)).ReadAsync("internal");
        Assert.Equal(["internal", "stable"], feeds.Select(f => f.Channel));
        Assert.False(partial);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await Assert.ThrowsAsync<HttpRequestException>(() => Client(new Servers([])).ReadAsync("stable"))).StatusCode);
        Assert.Empty((await Client(new Servers([])).ReadAsync("beta")).Feeds);   // none yet on its own channel
    }

    /// <summary>Some bytes, then nothing until cancelled.</summary>
    sealed class Stalling : Stream
    {
        bool sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!sent) { sent = true; buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
