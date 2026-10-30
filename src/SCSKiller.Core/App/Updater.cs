using System.Text;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace SCSKiller.Core.App;

/// <summary>Velopack in the app. Checks at start and every hour and downloads in the
/// background (nothing touches the install); applies only while no queue item runs anywhere (<see cref="Busy"/>): on
/// "Restart to update", and with <see cref="AutoInstall"/> on at the next start or at the tray's Quit. A build Velopack
/// didn't install (dev, the zip), a Flatpak and a package never check (<see cref="Installation"/>).</summary>
public static class Updater
{
    /// <summary>The app the updater runs in: set at its start, before anything reads <see cref="Updater"/>.</summary>
    public static class Host
    {
        public static IScsKiller Core { get; set; } = null!;
        public static Account Account { get; set; } = null!;
        public static Func<bool> Quitting { get; set; } = null!;
    }

    static readonly SemaphoreSlim One = new(1, 1);
    // no static here may build an UpdateManager: one needs Velopack's locator, which UpdateHooks.Run sets
    static string DataDir => UpdateHooks.DataDir;   // read at each use, as UpdateHooks.Log's is, so a record and its log share a folder
    // tests replace these: who updates this copy, the feed, and Velopack's locator (null: the one UpdateHooks.Run sets)
    internal static Lazy<Installation> Detected = new(() => Installation.Detect(Installation.ThisElsewhere, OperatingSystem.IsWindows(),
        () => Manager(UpdateChannels.Stable, false).IsInstalled), LazyThreadSafetyMode.PublicationOnly);
    internal static Func<IUpdateSource> Feed = () => new SignedFeedSource();
    internal static IVelopackLocator? TestLocator;
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
    static volatile FeedRead? lastRead;   // the last check that read every feed
    sealed record FeedRead(long At, string Channel);   // At: Environment.TickCount64
    static volatile bool asked;   // "Check for updates" was clicked: the running or next check shows its failure at once

    /// <summary>Raised on any thread after <see cref="Ready"/>, <see cref="Checking"/>, <see cref="Downloading"/>,
    /// <see cref="UpToDate"/> or <see cref="Problem"/> changed.</summary>
    public static event Action? Changed;
    public static string? Ready => Usable(ready) is { } d ? d.S.Version : null;

    static string Chosen() => UpdateChannels.Effective(Host.Core.Settings.UpdateChannel, AppVersion.Current.Channel, Host.Account.Status?.Ent);

    /// <summary>The token's entitlements; null while signed in and none was read yet.</summary>
    static IReadOnlyCollection<string>? Ent() => Host.Account.Status is { } s ? s.Ent : Host.Account.SignedIn ? null : [];

    /// <summary>A download is installed, and offered, only while its channel is still the chosen one and it is newer than
    /// this build. Every apply goes through here.</summary>
    static Download? Usable(Download? d) => AutoInstall.OffersRestart(d?.S, Host.Core.Settings.UpdateChannel, AppVersion.Current, Ent(), d?.BackToStable == true) ? d : null;

    /// <summary>The ready download's channel, or else the one the next check reads.</summary>
    public static string Channel => Usable(ready)?.Channel ?? Chosen();

    /// <summary>Whether the ready download, or else the next one, installs by itself at a start or quit.</summary>
    public static bool InstallsAutomatically => AutoInstall.On(Host.Core.Settings, Channel);

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
        if (s.Refused(FeedTrust.ReleaseKeys, UpdateChannels.Os) is { } why)
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
    static string? upToDateOn;
    /// <summary>The channel the last check found nothing newer on, while it is still the one checked; null: none.</summary>
    public static string? UpToDateOn => UpToDate && upToDateOn == Chosen() ? upToDateOn : null;
    public static string? Problem { get; private set; }
    /// <summary>"Restart to update" waits for the compile: the channel stays as it is meanwhile.</summary>
    public static bool Restarting { get; private set; }

    static Velo Manager(string channel, bool downgrade) =>
        new(Feed(), new UpdateOptions
        {
            ExplicitChannel = UpdateChannels.Signed(channel, UpdateChannels.Os), AllowVersionDowngrade = downgrade,
            MaximumDeltasBeforeFallback = -1,   // full packages only: a package rebuilt from deltas fails the feed's checksum
        }, TestLocator);

    /// <summary>Velopack's apply passes the package to Update.exe only while its file exists, else Update.exe takes the
    /// newest package on disk, whatever it is (<see cref="ApplyAsync"/>).</summary>
    sealed class Velo(IUpdateSource source, UpdateOptions options, IVelopackLocator? locator) : UpdateManager(source, options, locator)
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

        /// <summary>An interrupted download's bytes: nothing newer is offered to resume them for.</summary>
        public void ForgetPartial()
        {
            if (Locator.PackagesDir is not { } dir) return;
            try { PackageResume.Forget(dir); }
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
    /// fall back on; then, last, no compile running anywhere (<see cref="Busy.Handover"/>), the channel still the chosen
    /// one and <paramref name="still"/> for the download being applied; then <paramref name="apply"/>.</summary>
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
            if (Busy.Handover(DataDir, DateTimeOffset.UtcNow) is { } busy) return Undo(busy);   // writes the marker, kept only on null
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

    /// <summary>Who updates this copy. Read on first use, after UpdateHooks.Run unless it is a Flatpak or a package; a
    /// failure isn't kept, the next use tries again.</summary>
    public static Installation Installation => Detected.Value;

    /// <summary>Velopack installed this copy, and the updater runs.</summary>
    public static bool Installed => Installation.UpdatesItself;

    /// <summary>At app start (real data only): check soon and every hour.</summary>
    public static void Start()
    {
        if (Installation.UpdatedElsewhere) StandDown();
        if (!Installed) return;
        // not in the start's busy first seconds, and not again right after the start's own check, which a failure retries here
        timer = new Timer(_ => { if (!ReadRecently()) _ = CheckAsync(); }, null, TimeSpan.FromSeconds(30), UpdateFeeds.CheckEvery);
    }

    // a channel picked while another check ran (its call skipped) is still checked at the next tick
    static bool ReadRecently() => lastRead is { } r && r.Channel == Chosen() && Environment.TickCount64 - r.At < (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    // a download an AppImage run staged in a shared data folder never installs here; its package is in Velopack's folder, not ours
    static void StandDown()
    {
        Log(Installation.Kind == InstallKind.Flatpak ? "A Flatpak: its remote updates it, so this copy never checks"
            : $"A package: {Installation.Manager ?? "its package manager"} updates it, so this copy never checks");
        if (StagedUpdate.Load(DataDir) is { } s) Log($"The staged update to {s.Version} was dropped: this copy doesn't install updates");
        StagedUpdate.Forget(DataDir);
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
        if (!marked && Usable(ready) is { } u && Untouched() && AutoInstall.On(Host.Core.Settings, u.Channel)) await Host.Core.ScanAsync(CancellationToken.None);
        if (!marked && Usable(ready) is { } r && Untouched() && HeldBack(r.Channel) is null && BeginUpdate()
            && !await ApplyAsync(TimeSpan.Zero, (m, a) =>
            {
                var log = (Host.Core as ScsKiller)?.Log as AppLog;
                var before = log?.ExitReason;
                if (log != null) log.ExitReason = "restart to update";
                try { m.ApplyUpdatesAndRestart(a, args); }
                catch { if (log != null) log.ExitReason = before!; throw; }   // a failed restart is not the exit
            }, now => Untouched() && !GameRunning() && AutoInstall.On(Host.Core.Settings, now.Channel)))   // exits this process
        {
            EndUpdate();
            Log($"The update to {r.S.Version} didn't install at the start: {Problem ?? "the queue started or SCSKiller quit meanwhile."}");
        }
        if (Usable(ready) is null) await CheckAsync();   // the timer's first check skips while this one runs

        // on the UI thread, like Quit and the queue's changes: nothing slips in between this and the handover
        static bool Untouched() => !Host.Quitting() && !Host.Core.Queue.Any(q => q.Stage is not (Core.QueueStage.Done or Core.QueueStage.Failed) && !q.PlanCheck);
    }

    static void ClearMarker()
    {
        try { Busy.ClearApplying(DataDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static string? HeldBack(string channel) => AutoInstall.HeldBack(Host.Core.Settings, channel, Busy.IsHeld() || Host.Core.Compiling, OfflineBlocks(), GameRunning());

    static bool GameRunning() => Host.Core is ScsKiller k ? k.GameRunning() : Host.Core.Games.Any(g => g.Playing);

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
        string? reading = null;   // the channel whose feed is being read
        try
        {
            // reads the entitlements; stable's feed is on GitHub and needs no token, so a backend that's down mustn't stop it
            try { await Host.Account.GetAccessTokenAsync(); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or AccountException) { }
            var channel = backToStable ? UpdateChannels.Stable : Chosen();
            if (ready != null && Usable(ready) is null) Stage(null);   // another channel's download, even if this check fails
            var m = Manager(channel, backToStable);
            reading = channel;
            var found = await m.CheckForUpdatesAsync();
            reading = null;
            failures = 0;
            if (!SignedFeedSource.Partial) lastRead = new(Environment.TickCount64, channel);   // a feed it couldn't read is asked again at the next tick
            // not the installed version again: the internal channel lists stable's packages, and Velopack offers the same
            // version while the channels differ
            if (found is not { } info || info.TargetFullRelease.Version == m.CurrentVersion)
            {
                Log($"Checked {channel}: up to date ({m.CurrentVersion})");
                (UpToDate, upToDateOn) = (true, channel);
                m.ForgetPartial();
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
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            (Problem, failures) = (null, 0);
            if (reading != null)   // no feed published on this channel yet: read whole, the hourly tick asks again
            {
                Log($"Checked {reading}: no feed published yet");
                lastRead = new(Environment.TickCount64, reading);
            }
        }
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
            Problem = KeyringCause(e)?.Message ?? "Couldn't check for updates right now.";
        }
        finally
        {
            (Checking, Downloading, asked) = (false, null, false);
            One.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>The account's own line when the system keyring kept the token a channel's feed needs.</summary>
    internal static AccountException? KeyringCause(Exception? e) => e switch
    {
        AccountException { Keyring: true } a => a,
        AggregateException all => all.InnerExceptions.Select(KeyringCause).FirstOrDefault(a => a != null),
        null => null,
        _ => KeyringCause(e.InnerException),
    };

    // An apply stops every process under the install root, an offline session's cleanup helper too: from its start until it
    // is handed over, no offline session starts, and one that started before holds it back (ScsKiller.OfflineBlocksUpdate)
    static bool BeginUpdate() => Host.Core is not Core.App.ScsKiller k || k.BeginUpdate();
    static void EndUpdate() => (Host.Core as Core.App.ScsKiller)?.EndUpdate();
    static bool OfflineBlocks() => Host.Core is Core.App.ScsKiller { OfflineBlocksUpdate: true };

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
        if (!await ApplyAsync(TimeSpan.FromSeconds(30), (m, r) => m.WaitExitThenApplyUpdates(r, silent: true, restart: false), now => !GameRunning() && AutoInstall.On(Host.Core.Settings, now.Channel)))
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
            var queued = Host.Core.Queue.Where(q => q.Stage is not (Core.QueueStage.Done or Core.QueueStage.Failed) && !q.PlanCheck).Select(q => q.GameId).ToList();
            Host.Core.StopQueue();
            while (Host.Core.Compiling) await Task.Delay(250);
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
                var log = (Host.Core as ScsKiller)?.Log as AppLog;
                var before = log?.ExitReason;
                if (log != null) log.ExitReason = "restart to update";
                try { m.ApplyUpdatesAndRestart(r); }   // exits this process
                catch { if (log != null) log.ExitReason = before!; throw; }   // a failed restart is not the exit
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
        foreach (var id in ids.Where(id => Host.Core.Games.Any(g => g.Game.Id == id))) Host.Core.Enqueue(id);
        Host.Core.StartQueue();
    }

    /// <summary>The signed feed (§4.3): releases.&lt;signed channel&gt;.json (<see cref="UpdateChannels.Signed"/>) is used only
    /// after <see cref="FeedTrust"/> accepts its .sig; each package is fetched from its own version's channel
    /// (<see cref="UpdateFeeds.Package"/>), and Velopack then checks its SHA-256 against the signed feed.</summary>
    sealed class SignedFeedSource : IUpdateSource
    {
        static readonly FeedClient Feeds = new(RouteFailover.Default, Trust, () => Host.Account.GetAccessTokenAsync(), Log);

        /// <summary>The last feed left out a later channel it couldn't fetch: a version missing from it may still be offered.</summary>
        public static bool Partial { get; private set; }
        /// <summary>The signed feeds the last check read, as fetched: kept with its download (<see cref="StagedUpdate.Refused"/>).</summary>
        public static Dictionary<string, SignedFeed> Fetched { get; private set; } = [];

        /// <summary>The channel's feed, plus the full packages of the channels after it (<see cref="FeedClient.ReadAsync"/>).
        /// <paramref name="signed"/>: Velopack's channel, the signed one (<see cref="Manager"/>).</summary>
        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string signed, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            (Partial, Fetched) = (false, []);
            var channel = UpdateChannels.Of(signed, UpdateChannels.Os) ?? throw new FeedRejectedException($"unknown channel '{signed}'");
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
            // Velopack deletes its own .partial before each download: the resumable bytes live beside it
            await PackageResume.DownloadAsync(Path.GetDirectoryName(localFile)!, new(entry.FileName, entry.Size, entry.SHA256), localFile, async (from, ifRange, token) =>
            {
                var r = await Feeds.PackageAsync(version, entry.FileName, token, from, ifRange);
                // what the edge counts: a full package it answered with a body, a resumed one too (stable is on GitHub)
                if (version.Channel != UpdateChannels.Stable && entry.Type == VelopackAssetType.Full && r.Content.Headers.ContentLength != 0) Tried(t => t.Requested(DateTimeOffset.Now));
                return r;
            }, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), ct);
        }
    }
}
