using System.Text;
using SCSKiller.Core.App;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace SCSKiller.App;

/// <summary>Velopack in the app. Checks at start and every hour and downloads in the
/// background (nothing touches the install); applies only while no queue item runs anywhere (<see cref="Busy"/>): on
/// "Restart to update", and with <see cref="AutoInstall"/> on at the next start or at the tray's Quit. A build Velopack
/// didn't install (dev, the zip) never checks.</summary>
public static class Updater
{
    static readonly SemaphoreSlim One = new(1, 1);
    // no static here may build an UpdateManager: one needs Velopack's locator, which UpdateHooks.Run sets
    static readonly string DataDir = UpdateHooks.DataDir;
    static readonly AppStore Store = new(DataDir);
    static readonly FeedTrust Trust = new(Store, FeedTrust.ReleaseKeys);

    // bookkeeping only: a package-tries.json that can't be read or written never stops an update
    static PackageTries Tries()
    {
        try { return Store.LoadPackageTries(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }

    static void Tried(Action<PackageTries> change)
    {
        try
        {
            lock (Store)
            {
                var t = Store.LoadPackageTries();
                change(t);
                Store.SavePackageTries(t);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"Couldn't note the package download: {e.Message}"); }
    }
    static string ResumeFile => Path.Combine(DataDir, "resume-queue.txt");

    /// <summary>A finished download: the manager that downloaded it, its package and its record on disk.
    /// <paramref name="BackToStable"/>: "Go back to stable now"'s, never one read back from the disk.</summary>
    sealed record Download(Velo M, VelopackAsset R, StagedUpdate S, bool BackToStable = false)
    {
        public string Channel => S.Channel;
    }
    static volatile Download? ready;   // one reference: the UI reads it whole while a check replaces it
    static Timer? timer;
    static int failures;   // consecutive checks that couldn't reach the feed
    static volatile bool asked;   // "Check for updates" was clicked: the running or next check shows its failure at once

    /// <summary>Raised on any thread after <see cref="Ready"/>, <see cref="Checking"/>, <see cref="Downloading"/>,
    /// <see cref="UpToDate"/> or <see cref="Problem"/> changed.</summary>
    public static event Action? Changed;
    public static string? Ready => Usable(ready) is { } d ? d.S.Version : null;

    static string Chosen() => UpdateChannels.Effective(App.Core.Settings.UpdateChannel, AppVersion.Current.Channel, App.Account.Status?.Ent);

    /// <summary>The token's entitlements; null while signed in and none was read yet.</summary>
    static IReadOnlyCollection<string>? Ent() => App.Account.Status is { } s ? s.Ent : App.Account.SignedIn ? null : [];

    /// <summary>A download is installed, and offered, only while its channel is still the chosen one and it is newer than
    /// this build. Every apply goes through here.</summary>
    static Download? Usable(Download? d) => AutoInstall.OffersRestart(d?.S, App.Core.Settings.UpdateChannel, AppVersion.Current, Ent(), d?.BackToStable == true) ? d : null;

    /// <summary>The ready download's channel, or else the one the next check reads.</summary>
    public static string Channel => Usable(ready)?.Channel ?? Chosen();

    /// <summary>Whether the ready download, or else the next one, installs by itself at a start or quit.</summary>
    public static bool InstallsAutomatically => AutoInstall.On(App.Core.Settings, Channel);

    /// <summary>The ready download, and its record for the next start.</summary>
    static void Stage(Download? d)
    {
        ready = d;
        if (d == null) StagedUpdate.Forget(DataDir);
        else
            try { d.S.Save(DataDir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // this run still has it
    }

    /// <summary>The download an earlier run staged, once the signed feeds kept with it back it; its package is checked again
    /// before it is applied (ApplyAsync). One they don't back is dropped.</summary>
    static Download? Staged()
    {
        if (StagedUpdate.Load(DataDir) is not { } s) return null;
        if (s.Refused(FeedTrust.ReleaseKeys) is { } why)
        {
            Log($"The staged update to {s.Version} was dropped: {why}");
            Stage(null);
            return null;
        }
        var m = Manager(s.Channel, false);
        return new(m, new VelopackAsset
        {
            PackageId = m.AppId ?? "", Version = SemanticVersion.Parse(s.Version), Type = VelopackAssetType.Full,
            FileName = s.FileName, Size = s.Size, SHA256 = s.Sha256,
        }, s);
    }

    static void Log(string line) => UpdateHooks.Log(line);

    public static bool Checking { get; private set; }
    /// <summary>While <see cref="Checking"/>: the version whose package is being downloaded.</summary>
    public static string? Downloading { get; private set; }
    /// <summary>The last check reached the feed and found nothing newer.</summary>
    public static bool UpToDate { get; private set; }
    public static string? Problem { get; private set; }
    /// <summary>"Restart to update" waits for the compile: the channel stays as it is meanwhile.</summary>
    public static bool Restarting { get; private set; }

    static Velo Manager(string channel, bool downgrade) =>
        new(new SignedFeedSource(), new UpdateOptions
        {
            ExplicitChannel = channel, AllowVersionDowngrade = downgrade,
            MaximumDeltasBeforeFallback = -1,   // full packages only: a package rebuilt from deltas fails the feed's checksum
        });

    /// <summary>Velopack's apply passes the package to Update.exe only while its file exists, else Update.exe takes the
    /// newest package on disk, whatever it is (<see cref="ApplyAsync"/>).</summary>
    sealed class Velo(IUpdateSource source, UpdateOptions options) : UpdateManager(source, options)
    {
        string PathOf(VelopackAsset a) => Path.Combine(Locator.PackagesDir ?? "", a.FileName);

        /// <summary>The asset's file, its size and Velopack's own checksum.</summary>
        public async Task<bool> OnDisk(VelopackAsset asset)
        {
            var file = new FileInfo(PathOf(asset));
            if (!file.Exists || file.Length != asset.Size) return false;
            try { await VerifyPackageChecksumAsync(asset, file.FullName); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Velopack.Exceptions.ChecksumFailedException) { return false; }
        }

        /// <summary>A download that failed <see cref="OnDisk"/>: gone, so the next check downloads it again.</summary>
        public void Delete(VelopackAsset asset)
        {
            try { File.Delete(PathOf(asset)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>Every package but <paramref name="keep"/> and the installed version's: true once none other is left.</summary>
        public bool OnlyThis(VelopackAsset keep)
        {
            bool Other(VelopackAsset p) => p.FileName != keep.FileName && p.Version != CurrentVersion;
            foreach (var p in Locator.GetLocalPackages().Where(Other)) Delete(p);
            return !Locator.GetLocalPackages().Any(Other);
        }
    }

    /// <summary>The one way an update is applied (Quit, "Restart to update"), in order: the check's lock (a check may be
    /// replacing the download; <paramref name="wait"/> at most); the ready download read again; its file whole on disk,
    /// else deleted so the next check downloads it again; every other package deleted, so Update.exe has nothing else to
    /// fall back on; then, last, no compile running anywhere, the channel still the chosen one and <paramref name="still"/>
    /// for the download being applied; then <paramref name="apply"/>.</summary>
    static async Task<bool> ApplyAsync(TimeSpan wait, Action<Velo, VelopackAsset> apply, Func<Download, bool>? still = null)
    {
        if (!await One.WaitAsync(wait)) return false;
        try
        {
            if (Usable(ready) is not { M: var m, R: var r } d)
                return Fail("The update changed meanwhile. Restart to update again once it is ready.");
            if (!await m.OnDisk(r))
            {
                m.Delete(r);
                Stage(null);
                return Fail("The downloaded update is no longer whole on disk. It downloads again at the next check.");
            }
            if (!m.OnlyThis(r)) return Fail("An older downloaded update couldn't be removed. The update installs at a later quit.");
            // the marker first, then Busy: a compile worker holds Busy first, then reads the marker (ScsKiller.Work), so
            // one of the two always sees the other
            Busy.MarkApplying(DataDir, DateTimeOffset.UtcNow);
            if (Busy.IsHeld()) return Undo("A compile started meanwhile. The update installs when SCSKiller quits after it has finished.");
            if (Usable(ready) is null) return Undo("The update channel changed meanwhile. Restart to update again once it is ready.");
            if (still?.Invoke(d) == false) return Undo(null);
            if (OfflineBlocks()) return Undo(OfflineRunning);
            Log($"Handing the update to {r.Version} to Update.exe");
            apply(m, r);   // its preparation too (the resume file): a failure anywhere is undone below
            return true;
        }
        catch (Exception e)
        {
            var undone = Undo("Couldn't hand the update to the installer: " + e.Message);   // the marker first, whatever the log does
            Log($"Handing the update to Update.exe failed: {e}");
            return undone;
        }
        finally
        {
            One.Release();
            Changed?.Invoke();
        }

        static bool Fail(string? why)
        {
            Problem = why;
            return false;
        }

        // this process goes on: no marker stops the compiles, no resume file replays a queue that is still here
        static bool Undo(string? why)
        {
            try { Busy.ClearApplying(DataDir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // it expires (Busy.ApplyingFor)
            try { File.Delete(ResumeFile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return Fail(why);
        }
    }

    const string OfflineRunning = "A recording session's game or cleanup is running. The update installs once it has ended.";

    /// <summary>Read on first use, after UpdateHooks.Run; a failure isn't kept, the next use tries again.</summary>
    public static bool Installed => installed.Value;
    static readonly Lazy<bool> installed = new(() => Manager(UpdateChannels.Stable, false).IsInstalled, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>At app start (real data only): check soon and every hour.</summary>
    public static void Start()
    {
        if (!Installed) return;
        timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(30), UpdateFeeds.CheckEvery);   // not in the start's busy first seconds
    }

    /// <summary>App start (a launch, not a toast: its activation wouldn't survive the restart), before anything has begun:
    /// an update an earlier run downloaded installs from the disk, without the network (a logon may have none yet), once the
    /// first scan knows the games and none runs, and the app restarts into it with <paramref name="args"/>. Not after a handover whose new version never started: a failed
    /// apply must not restart the app in a loop, so it waits for "Restart to update" or a quit. Once the user has queued a
    /// game or quits, the update waits as well.</summary>
    public static async Task ApplyAtStartAsync(string[] args)
    {
        if (!Installed) return;
        var marked = Busy.Marked(DataDir);
        if (Staged() is { } d)
            switch (d.S.AtStart(AppVersion.Current, marked))
            {
                case StartStep.Apply: ready = d; break;
                case StartStep.Failed: Log($"The update to {d.S.Version} didn't install: {AppVersion.Current} started instead. It waits for Restart to update or a quit."); break;
                case StartStep.Older: Stage(null); break;
                case StartStep.Installed:
                    Log($"Updated to {AppVersion.Current}");
                    Stage(null);
                    if (marked) ClearMarker();   // the swap was done; only its hook didn't run
                    marked = false;
                    break;
            }
        else if (marked) Log($"An update didn't install: {AppVersion.Current} started instead.");
        Changed?.Invoke();
        // the games must be known to tell whether one runs: the scan also ends what an offline session left first
        if (!marked && Usable(ready) is { } u && Untouched() && AutoInstall.On(App.Core.Settings, u.Channel)) await App.Core.ScanAsync(CancellationToken.None);
        if (!marked && Usable(ready) is { } r && Untouched() && HeldBack(r.Channel) is null && BeginUpdate()
            && !await ApplyAsync(TimeSpan.Zero, (m, a) => m.ApplyUpdatesAndRestart(a, args), now => Untouched() && !GameRunning() && AutoInstall.On(App.Core.Settings, now.Channel)))   // exits this process
        {
            EndUpdate();
            Log($"The update to {r.S.Version} didn't install at the start: {Problem ?? "the queue started or SCSKiller quit meanwhile."}");
        }
        if (Usable(ready) is null) await CheckAsync();   // the timer's first check skips while this one runs

        // on the UI thread, like Quit and the queue's changes: nothing slips in between this and the handover
        static bool Untouched() => !App.Quitting && !App.Core.Queue.Any(q => q.Stage is not (Core.QueueStage.Done or Core.QueueStage.Failed) && !q.PlanCheck);
    }

    static void ClearMarker()
    {
        try { Busy.ClearApplying(DataDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static string? HeldBack(string channel) => AutoInstall.HeldBack(App.Core.Settings, channel, Busy.IsHeld() || App.Core.Compiling, OfflineBlocks(), GameRunning());

    static bool GameRunning() => App.Core is ScsKiller k ? k.GameRunning() : App.Core.Games.Any(g => g.Playing);

    /// <summary>About's "Check for updates": checks and downloads now. A click while a check runs joins it, and that
    /// check's failure shows at once.</summary>
    public static Task CheckNowAsync()
    {
        asked = true;
        return CheckAsync();
    }

    /// <summary>Checks the effective channel's signed feed and downloads a newer version. <paramref name="backToStable"/>:
    /// "Go back to stable now", the stable feed with a downgrade allowed once.</summary>
    public static async Task CheckAsync(bool backToStable = false)
    {
        if (!Installed) return;
        if (!await One.WaitAsync(backToStable ? Timeout.InfiniteTimeSpan : TimeSpan.Zero)) return;   // the user's click waits for a running check
        (Checking, Problem, UpToDate) = (true, null, false);
        Changed?.Invoke();
        try
        {
            // reads the entitlements; stable's feed is on GitHub and needs no token, so a backend that's down mustn't stop it
            try { await App.Account.GetAccessTokenAsync(); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or AccountException) { }
            var channel = backToStable ? UpdateChannels.Stable : Chosen();
            if (ready != null && Usable(ready) is null) Stage(null);   // another channel's download, even if this check fails
            var m = Manager(channel, backToStable);
            var found = await m.CheckForUpdatesAsync();
            failures = 0;
            // not the installed version again: the internal channel lists stable's packages, and Velopack offers the same
            // version while the channels differ
            if (found is not { } info || info.TargetFullRelease.Version == m.CurrentVersion)
            {
                Log($"Checked {channel}: up to date ({m.CurrentVersion})");
                UpToDate = true;
                if (ready?.Channel == channel && !SignedFeedSource.Partial) Stage(null);   // its feed no longer offers it: a release taken back isn't installed
            }
            else
            {
                if (!await m.OnDisk(info.TargetFullRelease))
                {
                    if (UpdateFeeds.PackageHold(Tries(), DateTimeOffset.Now, asked, AppVersion.Parse(info.TargetFullRelease.Version.ToString())?.Channel != UpdateChannels.Stable) is { } until)
                    {
                        Log($"The download of {info.TargetFullRelease.Version} waits until {until:g}");
                        Problem = asked ? $"The update was downloaded too many times today. SCSKiller tries again at {until:g}." : null;   // a click skips only the backoff
                        return;
                    }
                    Downloading = info.TargetFullRelease.Version.ToString();
                    Changed?.Invoke();
                }
                Log($"Checked {channel}: {info.TargetFullRelease.Version} is available");
                try { await m.DownloadUpdatesAsync(info); }   // a package already whole on disk is not fetched again
                catch
                {
                    Tried(t => t.Failed(DateTimeOffset.Now));
                    throw;
                }
                Tried(t => t.Succeeded());
                var t = info.TargetFullRelease;
                Log($"Downloaded {t.Version} ({t.FileName})");
                if (channel == Chosen()) Stage(new(m, t, new(channel, t.Version.ToString(), t.FileName, t.Size, t.SHA256, SignedFeedSource.Fetched), backToStable));   // the choice may have changed meanwhile
            }
        }
        catch (FeedRejectedException e)
        {
            Log($"The update feed failed its signature check: {e.Message}");
            Problem = "The update feed failed its signature check: " + e.Message;
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { Problem = null; failures = 0; }   // no feed published on this channel yet
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            Log("The update server asked SCSKiller to wait (429)");
            Problem = asked ? "The update server asked SCSKiller to wait. It checks again by itself later." : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            Log($"Couldn't check for updates: {e.GetType().Name}: {e.Message}");
            // offline or a hiccup: the next check retries; say so only once it has kept failing for a day
            Problem = ++failures >= 24 ? "Couldn't reach the update server for a while. SCSKiller keeps trying."
                : asked ? "Couldn't reach the update server. SCSKiller tries again within the hour." : null;
        }
        catch (Exception e)
        {
            Log($"Couldn't check for updates: {e}");
            Problem = "Couldn't check for updates right now.";
        }
        finally
        {
            (Checking, Downloading, asked) = (false, null, false);
            One.Release();
            Changed?.Invoke();
        }
    }

    // An apply stops every process under the install root, an offline session's cleanup helper too: from its start until it
    // is handed over, no offline session starts, and one that started before holds it back (ScsKiller.OfflineBlocksUpdate)
    static bool BeginUpdate() => App.Core is not Core.App.ScsKiller k || k.BeginUpdate();
    static void EndUpdate() => (App.Core as Core.App.ScsKiller)?.EndUpdate();
    static bool OfflineBlocks() => App.Core is Core.App.ScsKiller { OfflineBlocksUpdate: true };

    /// <summary>App exit (the tray's Quit, never Windows' shutdown): hands a downloaded update to Update.exe, which swaps it
    /// in once this process has exited. Not with <see cref="AutoInstall"/> off, nor while a compile runs (here or in the
    /// CLI), an offline session is pending, a game runs or the first scan hasn't listed the games yet, nor while a check
    /// still downloads after 30 s: then at the next start.</summary>
    public static async Task ApplyOnExitAsync()
    {
        if (Usable(ready) is not { } d) return;
        if (HeldBack(d.Channel) is { } why)
        {
            Log($"The update to {d.S.Version} waits for the next start: {why}");
            return;
        }
        if (!BeginUpdate()) return;
        if (!await ApplyAsync(TimeSpan.FromSeconds(30), (m, r) => m.WaitExitThenApplyUpdates(r, silent: true, restart: false), now => !GameRunning() && AutoInstall.On(App.Core.Settings, now.Channel)))
        {
            EndUpdate();
            Log($"The update to {d.S.Version} didn't install at the quit: {Problem ?? "a game started meanwhile."}");
        }
    }

    /// <summary>"Restart to update": stops the queue gracefully (in-flight compiles finish, the driver writes its cache),
    /// waits for it, remembers what was queued, and restarts into the new version, which resumes the queue where it
    /// stopped (each game's ResumeAt). False (and <see cref="Problem"/>) when the driver-update task's compile still runs.</summary>
    public static async Task<bool> RestartAsync()
    {
        if (Usable(ready) is null) return false;
        if (!BeginUpdate())
        {
            Problem = OfflineRunning;
            Changed?.Invoke();
            return false;
        }
        (Restarting, Problem) = (true, null);
        Changed?.Invoke();
        var applied = false;
        try
        {
            // plan checks aren't resumed: the next version's scan queues its own
            var queued = App.Core.Queue.Where(q => q.Stage is not (Core.QueueStage.Done or Core.QueueStage.Failed) && !q.PlanCheck).Select(q => q.GameId).ToList();
            App.Core.StopQueue();
            while (App.Core.Compiling) await Task.Delay(250);
            for (var i = 0; i < 20 && Busy.IsHeld(); i++) await Task.Delay(100);   // our worker lets go just after the item ends
            if (Busy.IsHeld())
            {
                Problem = "The background rebuild after a driver update is compiling. The update installs when SCSKiller quits after it has finished.";
                Log($"Restart to update: {Problem}");
                return false;
            }
            applied = await ApplyAsync(Timeout.InfiniteTimeSpan, (m, r) =>
            {
                if (queued.Count > 0) File.WriteAllLines(ResumeFile, queued);
                m.ApplyUpdatesAndRestart(r);   // exits this process
            });
            if (!applied) Log($"Restart to update didn't install it: {Problem}");
            return applied;
        }
        finally
        {
            if (!applied) EndUpdate();
            Restarting = false;
            Changed?.Invoke();
        }
    }

    public static bool HasResume => File.Exists(ResumeFile);

    /// <summary>After "Restart to update": the queue as it was, each game from where its warm stopped.</summary>
    public static void ResumeQueue()
    {
        if (!HasResume) return;
        var ids = File.ReadAllLines(ResumeFile);
        File.Delete(ResumeFile);
        foreach (var id in ids.Where(id => App.Core.Games.Any(g => g.Game.Id == id))) App.Core.Enqueue(id);
        App.Core.StartQueue();
    }

    /// <summary>The signed feed (§4.3): releases.&lt;channel&gt;.json is used only after <see cref="FeedTrust"/> accepts its
    /// .sig; each package is fetched from its own version's channel (<see cref="UpdateFeeds.Package"/>), and Velopack then
    /// checks its SHA-256 against the signed feed.</summary>
    sealed class SignedFeedSource : IUpdateSource
    {
        static readonly FeedClient Feeds = new(RouteFailover.Default, Trust, () => App.Account.GetAccessTokenAsync(), Log);

        /// <summary>The last feed left out a later channel it couldn't fetch: a version missing from it may still be offered.</summary>
        public static bool Partial { get; private set; }
        /// <summary>The signed feeds the last check read, as fetched: kept with its download (<see cref="StagedUpdate.Refused"/>).</summary>
        public static Dictionary<string, SignedFeed> Fetched { get; private set; } = [];

        /// <summary>The channel's feed, plus the full packages of the channels after it (<see cref="FeedClient.ReadAsync"/>).</summary>
        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            (Partial, Fetched) = (false, []);
            var (feeds, partial) = await Feeds.ReadAsync(channel);
            Partial = partial;
            var parsed = new VelopackAssetFeed { Assets = [] };
            foreach (var (c, f) in feeds)
            {
                Fetched[c] = f;
                var more = VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(f.Feed));
                // Velopack falls back to SHA-1 for an asset without SHA-256: a signed feed must pin every package by SHA-256
                if (more.Assets.Any(a => string.IsNullOrEmpty(a.SHA256))) throw new FeedRejectedException("a package without a SHA-256");
                var have = parsed.Assets.Select(a => a.FileName).ToHashSet();
                parsed.Assets = [.. parsed.Assets, .. more.Assets.Where(a => c == channel || a.Type == VelopackAssetType.Full && !have.Contains(a.FileName))];
            }
            return parsed;
        }

        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset entry, string localFile, Action<int> progress, CancellationToken ct = default)
        {
            var version = AppVersion.Parse(entry.Version.ToString()) ?? throw new FeedRejectedException($"version '{entry.Version}'");
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headers.CancelAfter(TimeSpan.FromMinutes(1));
            using var r = await Feeds.PackageAsync(version, entry.FileName, headers.Token);
            // what the edge counts: a full package it answered with a body (stable is on GitHub)
            if (version.Channel != UpdateChannels.Stable && entry.Type == VelopackAssetType.Full && r.Content.Headers.ContentLength != 0) Tried(t => t.Requested(DateTimeOffset.Now));
            headers.CancelAfter(Timeout.InfiniteTimeSpan);   // the body has its own stall limit
            await using var file = File.Create(localFile);
            await using var body = await r.Content.ReadAsStreamAsync(ct);
            await UpdateFeeds.Download(body, file, entry.Size, TimeSpan.FromMinutes(2), ct);
        }
    }
}
