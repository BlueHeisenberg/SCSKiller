using System.Collections.Concurrent;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Vulkan;
using static SCSKiller.Core.BeforeAfterStage;

namespace SCSKiller.Core.App;

public sealed partial class ScsKiller
{
    public const string NoStartNote = "Didn't see the game start";
    public const string LostNote = "The game started again before SCSKiller read the run, so it's lost";
    public const string DriverVoidNote = "The driver changed since the before recording, start over";
    public const string BuildVoidNote = "The game was updated since the before recording, start over";
    public const string NotCompiledNote = "The compile didn't finish, compile again";
    public const string StoppedNote = "The compile was stopped";
    public const string DirtyNote = "The game ran after the compile, Continue compiles again first";
    public const string DriverRedoNote = "The driver was updated, play the before run again";
    public const string BuildRedoNote = "The game was updated, play the before run again";
    public const string RecorderNotReadyNote = "The recorder isn't ready, so the run wouldn't be measured: ";
    public const string OldCacheNote = "Old driver cache files were in use: close the game and play the before run again";
    public const string OldCacheRedoNote = "Old driver cache files were in use, play the before run again";
    public const string UnseenRedoNote = "SCSKiller wasn't running during the before run, so it couldn't check the driver cache: play it again";
    public const string FramesStoppedNote = "Frame times stopped when frame generation started, so this run can't be measured. Turn frame generation off for the before and after recording";
    public const string RestoreFailedNote = "Couldn't put back {0} of the driver's cache files: they're in {1}";
    public const string AsideHeldNote = "The game's shader cache is still in use, try again in a minute";
    public const string ClearHeldNote = "Couldn't clear the cache: " + AsideHeldNote;
    public const string PutBackRunningNote = "{0} is running, its shader cache goes back once it exits";
    public const string PutBackInUseNote = "{0}'s shader cache goes back once nothing uses it";
    public const string VulkanNotColdNote = "Some of NVIDIA's Vulkan cache can't be set aside, so the before run may not start cold";

    /// <summary>A note the pair moves on from by itself or with the next run, so the app keeps waiting: a run to play again, a
    /// put-back that waits, a start not seen yet. Any other note needs the user.</summary>
    public static bool RetryNote(string? note) => note is null or NoStartNote or OldCacheNote or OldCacheRedoNote or UnseenRedoNote or DriverRedoNote
        or BuildRedoNote or FramesStoppedNote or LostNote or AsideHeldNote or ClearHeldNote or VulkanNotColdNote
        || note.StartsWith("The before run ", StringComparison.Ordinal) || note.StartsWith("The after run ", StringComparison.Ordinal);

    // PlayReadEvery: every poll, so the app's countdown stays within a poll of the one in the game, which counts live
    static readonly TimeSpan NoStartAfter = TimeSpan.FromMinutes(5), PlayReadEvery = TimeSpan.FromSeconds(2);

    readonly Dictionary<string, DateTimeOffset> _pairPolled = [];   // game id -> its last armed or playing check (PollGames only)
    readonly ConcurrentDictionary<string, double> _played = new();   // game id -> play counted so far in its run
    readonly ConcurrentDictionary<string, Task> _learning = new();
    readonly ConcurrentDictionary<string, byte> _pairInstall = new();   // game ids whose reconcile installs this build's recorder for a pair   // game id -> its LearnWhilePlaying (the before run's exit waits for it)

    public string? BeforeAfterBlocked(string gameId)
    {
        var s = Find(gameId);
        var rec = Store.LoadGame(gameId);
        bool compiling;
        lock (_lock) compiling = _current == gameId;
        // NVIDIA, DirectX 11: the recorder isn't in the game, the pair puts it in (Frames11)
        var frames11 = Frames11(s.Engine, Vendor.Caps);
        var skip = frames11 ? PairRecorderSkip(s, rec) : s.RecorderSkip;
        // the Vulkan layer times frames as the DirectX recorder does
        var vulkan = VulkanRoute(s.Game, s.Engine);
        return !Settings.BeforeAfter ? "Before and after recording is off in Settings"
            : s.Engine?.GraphicsApi is not { } api || !api.Contains("D3D12") && !api.StartsWith("D3D11") && !vulkan
                ? s is { Status: GameStatus.Unsupported, Engine: { } e } && Vulkan.VulkanPlan.IsVulkan(e) ? s.StatusReason : "Only for DirectX 12 and DirectX 11 games"
            : s.AnyAntiCheat ? "Not for games with anti-cheat"
            : s.NoStutter != null ? "Not for games without shader compilation stutter"
            : !(frames11 ? RecorderWanted(s.RecorderOverride, Settings.RecordAllGames, skip, true) : s.RecorderEffective)
                ? skip != null ? $"The recorder can't run in this game: {skip}" : "Turn the recorder on for this game first"
            // a note the recorder's set-up already fixed ("play again") doesn't keep the next launch from being recorded
            : s.RecorderRefused is { } refused && refused != GameChangedNote && !refused.StartsWith(RanOtherExe) ? refused
            : s.Status is not (GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale or GameStatus.NeedsRecording) ? s.StatusReason
            : s.Playing || GameRunning(s.Game) ? $"{s.Game.Name} is running"
            : compiling ? "Wait for the compile to finish" + (QueueItemOf(gameId)?.StartedAt is { } at ? $" (started {at.LocalDateTime:t})" : "")
            : rec.BeforeAfter is { Stage: not Done } ? "A before and after recording is already in progress"
            // Clear cache would refuse
            : AppCache != null && DriverKeys(s.Game, rec).Keys is { Count: > 0 } keys && SharedWith(gameId, keys) is { Count: > 0 } shared
                ? $"Its driver cache is shared with {string.Join(", ", shared.Select(x => x.Name))}"
            : null;
    }

    public void StartBeforeAfter(string gameId, int minutes, bool overlay = true, bool max = false)
    {
        if (BeforeAfterBlocked(gameId) is { } why) throw new InvalidOperationException(why);
        var g = Find(gameId).Game;
        if (!Frames11(Find(gameId).Engine, Vendor.Caps)) BeforeAfterRecorderReady(gameId);   // refused before anything is cleared; the frames-only one goes in with the stage
        Remove(gameId);
        if (Store.LoadGame(gameId).BeforeAfter is { } old) Unpin(gameId, old);
        lock (_setAsideLock)   // a poll puts back files set aside for a run that isn't armed
        {
            var (survivors, gap) = ClearBeforeRun(gameId);
            var rec = Store.LoadGame(gameId);
            string? note = null;
            NotColdNote(gameId, ref note);
            rec.BeforeAfter = new BeforeAfterRecord { Stage = BeforeArmed, Minutes = minutes, ArmedAt = Clock(), DriverId = DriverId, Build = BuildNow(g), Overlay = overlay, Max = max && MaxCompileAdds(Vendor.Caps, Find(gameId).Engine),
                Survivors = survivors, ClearGap = gap, Problem = note };
            _played.TryRemove(gameId, out _);
            Changed(g, rec);
        }
    }

    /// <summary>Right before a run's launch: the recorder installed under the run's name and armed, else
    /// InvalidOperationException with why. The game's state only says it should be: a scan publishes the states before it
    /// installs, and a game's exit installs what its launch missed.</summary>
    public void BeforeAfterRecorderReady(string gameId)
    {
        if (!ManageRecorders) return;   // game folders left alone (the unattended start): no pair starts from there
        _pairInstall[gameId] = 0;   // this build's recorder, even over a newer build's
        try { ReconcileRecorders(gameId); }
        finally { _pairInstall.TryRemove(gameId, out _); }
        var s = Find(gameId);
        var rec = Store.LoadGame(gameId);
        var dir = Path.GetDirectoryName(s.Game.ExePath)!;
        var dll = Path.Combine(dir, s.AgsHost ? AgsName : As11(s.Engine, rec, Vendor.Caps) ? Dll11 : Dll12);
        bool installed, current, iniReady;
        try
        {
            if (VulkanRoute(s.Game, s.Engine))   // the layer's copy registered for the exe's folder, this build's bytes
            {
                var layer = rec.VulkanLayer is { } m ? Path.Combine(Path.GetDirectoryName(m)!, Vulkan.VulkanLayer.Dll) : null;
                installed = layer != null && File.Exists(rec.VulkanLayer) && Vulkan.VulkanLayer.Registered(rec.VulkanLayer!, s.Game) && VulkanIn(s.Game, rec);
                current = installed && (VulkanLayerSource is not { } src || !File.Exists(src) || SameBytes(layer!, src));
            }
            else
            {
                installed = IsOurProxy(dll);
                current = installed && (ProxySha(Path.GetFileName(dll)) is not { } sha || Sha256(dll) == sha);
            }
            // the run's own lines (the countdown's): an ini SCSKiller couldn't write would leave the run without them
            var lines = RunIni(s.Game, rec.BeforeAfter);
            iniReady = lines.Length == 0 || File.Exists(Path.Combine(dir, "scskiller.ini")) && File.ReadAllText(Path.Combine(dir, "scskiller.ini")).Contains(lines, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { installed = current = iniReady = false; }
        if (installed && current && iniReady && Attested(s.Game)) return;
        throw new InvalidOperationException(RecorderNotReadyNote + (s.RecorderSkip ?? (s.RecorderNote == NoteForeignIni ? "its scskiller.ini isn't SCSKiller's" : s.RecorderNote) ?? _unstamped.GetValueOrDefault(gameId)
            ?? (!s.RecorderEffective ? "it's off for this game"
                : installed && !current ? "another SCSKiller's recorder is in the game and couldn't be replaced"
                : installed && !iniReady ? "its scskiller.ini isn't SCSKiller's"
                : installed ? "the game's folder is still being checked, try again in a moment" : "it couldn't be installed")));
    }

    public void ContinueBeforeAfter(string gameId)
    {
        var s = Find(gameId);
        var rec = Store.LoadGame(gameId);
        if (rec.BeforeAfter is { Stage: BeforeArmed, ClearPending: true } pending)
        {
            Idle(s.Game);
            lock (_setAsideLock)   // a poll puts back files set aside for a run that isn't armed
            {
                try { (pending.Survivors, pending.ClearGap) = ClearBeforeRun(gameId); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new InvalidOperationException($"Couldn't clear the cache: {e.Message}", e); }
                string? note = null;
                NotColdNote(gameId, ref note);
                (pending.ArmedAt, pending.ClearPending, pending.Problem) = (Clock(), false, note);
                Changed(s.Game, rec);
            }
            return;
        }
        if (rec.BeforeAfter is not { Stage: AfterReady or BeforeAfterStage.Compiling } ba) throw new InvalidOperationException("There's no after recording to continue");
        if (Voided(s.Game, ba) is { } why) throw new InvalidOperationException(why);
        Idle(s.Game);
        // the after run must read the game's cache set aside for the before run, not one made since
        if (SetAside() is { AfterRunReads: true } q && q.PendingGame == gameId)
            lock (_setAsideLock)
                if (q.Restore() is var left && q.PendingGame == gameId)
                    throw new InvalidOperationException(left.Count > 0 ? F(RestoreFailedNote, left.Count, q.Folder) : AsideHeldNote);
        ba.Dirty |= LaunchesAfter(s.Game, rec, ba.ArmedAt).After.Count > 0;   // played while the app was closed
        if (ba.Stage == BeforeAfterStage.Compiling || ba.Dirty || s.Status != GameStatus.Warmed)
        {
            Recompile(s.Game, rec, null);
            return;
        }
        (ba.Stage, ba.ArmedAt, ba.Problem) = (AfterArmed, Clock(), null);
        Changed(s.Game, rec);
    }

    public void RecordAfterAgain(string gameId)
    {
        var g = Find(gameId).Game;
        var rec = Store.LoadGame(gameId);
        if (rec.BeforeAfter is not { Stage: Done } ba) throw new InvalidOperationException("There's no finished before and after recording");
        if (Voided(g, ba) is { } why) throw new InvalidOperationException(why);
        Idle(g);
        if (ba.After is { } after) Snapshots.Pin(Store, gameId, after, null);
        ba.After = null;
        Recompile(g, rec, null);
    }

    public void EndBeforeAfter(string gameId, bool compile)
    {
        var g = Find(gameId).Game;
        var rec = Store.LoadGame(gameId);
        if (rec.BeforeAfter is { } ba)
        {
            Unpin(gameId, ba);
            if (ba.Max) lock (_lock) _maxNext.Remove(gameId);   // the pair's Max compile, not one the game page asked for after it
        }
        rec.BeforeAfter = null;
        _played.TryRemove(gameId, out _);
        Changed(g, rec);
        if (compile) Compile(gameId);
    }

    /// <summary>AntiCheatFound, a finished check's: a pair not done yet ends (a done one is a report and stays).
    /// <paramref name="installing"/>: Install's record, which it may save after this.</summary>
    void EndPair(Game g, GameRecord? installing)
    {
        BeforeAfterRecord? ended = null;
        try
        {
            PairChange(g.Id, (r, ba) =>
            {
                if (ba.Stage == Done) return false;
                (ended, r.BeforeAfter) = (ba, null);
                return true;
            });
            if (ended != null) Unpin(g.Id, ended);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't end its before and after recording: {e.Message}"); }
        if (installing?.BeforeAfter is { Stage: not Done }) installing.BeforeAfter = null;
        _played.TryRemove(g.Id, out _);
        lock (_lock)
            if (_games.FindIndex(x => x.Game.Id == g.Id) is var i and >= 0 && _games[i].BeforeAfter is { Stage: not Done })
                _games[i] = _games[i] with { BeforeAfter = null };
    }

    public BeforeAfterResult? BeforeAfterReport(string gameId)
    {
        if (Store.LoadGame(gameId).BeforeAfter is not { Stage: Done, Before: { } b, After: { } a } ba) return null;
        var (before, after) = (Snapshots.Read(Store, gameId, b), Snapshots.Read(Store, gameId, a));
        // AMD's DirectX 11 cache: a key the clear didn't know may have been read without being held open, or (set aside but
        // the key made during the run not tied to the game) wasn't known to the clear before the compile
        return before == null || after == null ? null
            : new(before, after, BeforeAfter.Compare(before, after, max: ba.Max,
                notCold: AppCache is AmdAppCache && before.D3D11 ? ba.ClearGap != false ? BeforeAfter.MaybeWarm : null
                    : OperatingSystem.IsWindows() && VulkanRoute(Find(gameId).Game, Find(gameId).Engine) ? ba.ClearGap != false ? BeforeAfter.VulkanNotCold : null
                    : VulkanNotColdReport(gameId),
                afterHelped: AppCache is AmdAppCache && before.D3D11 && ba.DxShared));
    }

    /// <summary>Removing the game deletes its snapshots, the pinned pair's too (Clear recording keeps them: they're reports).</summary>
    void DeleteSessionSnapshots(string gameId)
    {
        try { Directory.Delete(Path.Combine(Store.GameDir(gameId), "sessions"), true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{gameId}: couldn't delete its session snapshots: {e.Message}"); }
    }

    /// <summary>Why a compile (<paramref name="compiling"/>) or Clear cache from outside the flow would spoil the pair; null =
    /// neither does. A compile before the before run warms what it must find cold; a clear after it drops the compile the
    /// after run measures.</summary>
    string? PairHoldsCache(string gameId, bool compiling = false) => Store.LoadGame(gameId).BeforeAfter?.Stage switch
    {
        BeforeArmed or BeforePlaying when compiling => "Its before recording is still to come, end the before and after recording to compile now",
        BeforeAfterStage.Compiling or AfterReady or AfterArmed or AfterPlaying when !compiling => "Its before and after recording needs this cache, end it first",
        _ => null,
    };

    void Idle(Game g)
    {
        if (IsPlaying(g.Id) || GameRunning(g)) throw new InvalidOperationException($"{g.Name} is running");
        if (QueueItemOf(g.Id) is { } q && !Finished(q.Stage)) throw new InvalidOperationException("Wait for the compile to finish");
    }

    void Unpin(string gameId, BeforeAfterRecord ba)
    {
        foreach (var t in new[] { ba.Before, ba.After }.OfType<long>()) Snapshots.Pin(Store, gameId, t, null);
    }

    QueueItem? QueueItemOf(string gameId)
    {
        lock (_lock) return _queue.FirstOrDefault(q => q.GameId == gameId);
    }

    static string BuildNow(Game g) => g.Version ?? ExeStamp(g);

    string? Voided(Game g, BeforeAfterRecord ba) => IdentityChange(ba, DriverId, BuildNow(g));

    static string? IdentityChange(BeforeAfterRecord ba, string? driverId, string? build) =>
        ba.DriverId != null && driverId != null && ba.DriverId != driverId ? DriverVoidNote
        : ba.Build != null && build != null && ba.Build != build ? BuildVoidNote
        : null;

    /// <summary>Every stage change ends here; the recorder ini may carry the stage (written only while the game is closed).</summary>
    void Changed(Game g, GameRecord rec)
    {
        Store.SaveGame(g.Id, rec);
        UpdateRecorderIni(g, rec);
        Refresh(g);   // also catches up with a compile that ended before the save
        if (ManageRecorders && Frames11(Games.FirstOrDefault(s => s.Game.Id == g.Id)?.Engine, Vendor.Caps))   // its recorder follows the pair
            try { ReconcileRecorders(g.Id); }
            catch (Exception e) { Log?.Report($"{g.Name}: reconciling the recorder failed: {e.Message}"); }
    }

    /// <summary>Clear cache (the game's own too), then a compile first in the queue. A clear that fails compiles nothing:
    /// the after run would be flattered by what the cache still holds.</summary>
    void Recompile(Game g, GameRecord rec, string? problem)
    {
        var ba = rec.BeforeAfter!;
        (ba.Stage, ba.Dirty, ba.ArmedAt, ba.Problem) = (BeforeAfterStage.Compiling, false, Clock(), problem);
        try { ClearCache(g.Id, gamePrecache: true); }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ba.Problem = $"Couldn't clear the cache: {e.Message}";
            if (QueueItemOf(g.Id) is { } q && Finished(q.Stage)) Remove(g.Id);   // an earlier compile's Done isn't this one's
            Changed(g, rec);
            return;
        }
        if (ba.Max) lock (_lock) _maxNext.Add(g.Id);
        Compile(g.Id, pair: true);
        MoveInQueue(g.Id, 0);
        Changed(g, rec);
    }

    FrameReport? FramesNow(Game g, GameRecord rec, double? playCapMs = null) => Frames(SessionDir(g), Path.GetFileName(g.ExePath),
        OwnDriver == GpuVendor.Nvidia ? SessionLog.ReadRayQueryKeys(RayQueryKeysPath(g.Id)) : null, rec.LastPlay, playCapMs);

    /// <summary>The game's launches stamped after <paramref name="since"/>, oldest first: the snapshots, the csv's launches
    /// and the frame log's (<paramref name="Last"/>, 0 = none), which keeps only the last launch.</summary>
    (List<long> After, long Last) LaunchesAfter(Game g, GameRecord rec, DateTimeOffset since)
    {
        var from = since.ToUnixTimeMilliseconds();
        var last = FramesNow(g, rec)?.LaunchUnixMs ?? 0;
        var csv = Path.Combine(SessionDir(g), "scskiller_creates.csv");
        var launches = File.Exists(csv)
            ? SessionLog.Launches(csv).Where(l => l.StartT != null && !SessionLog.OtherExe(l.Exe, Path.GetFileName(g.ExePath))).Select(l => l.Start ?? 0)
            : [];
        return (Snapshots.List(Store, g.Id).Select(s => s.LaunchUnixMs).Concat(launches).Append(last).Where(t => t > from).Distinct().Order().ToList(), last);
    }

    /// <summary>A before and after run's length in ms when <paramref name="frames"/> is a run's launch, the one playing or
    /// a pinned one (a later scan writes its snapshot again): both runs are compared over the same play.</summary>
    static double? PairCapMs(GameRecord rec, FrameReport frames) => rec.BeforeAfter is { } ba
        && (frames.LaunchUnixMs == ba.Before || frames.LaunchUnixMs == ba.After
            || ba.Stage is BeforeArmed or BeforePlaying or AfterArmed or AfterPlaying && frames.LaunchUnixMs > ba.ArmedAt.ToUnixTimeMilliseconds())
        ? ba.Minutes * 60_000.0 : null;

    /// <summary>The run: the first launch stamped after ArmedAt. Lost when the frame log holds a later one.</summary>
    (SessionSnapshot? Run, string? Lost) RunOf(Game g, GameRecord rec, BeforeAfterRecord ba)
    {
        var (after, last) = LaunchesAfter(g, rec, ba.ArmedAt);
        if (after.Count == 0) return (null, null);
        return last > after[0] ? (null, LostNote) : (Snapshots.Read(Store, g.Id, after[0]), null);
    }

    /// <summary>PollGames, a start: the armed run begins, or the game ran outside the flow. The info to show; null = unchanged.</summary>
    BeforeAfterInfo? BeforeAfterStarted(string gameId)
    {
        BeforeAfterInfo? shown = null;
        PairChange(gameId, (r, ba) =>
        {
            switch (ba.Stage)
            {
                case BeforeArmed or AfterArmed when !ba.ClearPending:
                    (ba.Stage, ba.Problem) = (ba.Stage == BeforeArmed ? BeforePlaying : AfterPlaying, null);
                    _played.TryRemove(gameId, out _);
                    shown = new(ba.Stage, ba.Minutes, null, 0);
                    return true;
                case BeforeAfterStage.Compiling or AfterReady when !ba.Dirty:
                    return ba.Dirty = true;
                default:
                    return false;
            }
        });
        return shown;
    }

    /// <summary>Load, <paramref name="change"/> (true = changed) and save under the record's lock, for the writers that
    /// race the others (the watcher's, the compile's end): one never puts back a pair another just changed or ended.</summary>
    GameRecord? PairChange(string gameId, Func<GameRecord, BeforeAfterRecord, bool> change)
    {
        GameRecord? rec = null;
        AppStore.Locked(Path.Combine(Store.GameDir(gameId), "state.json"), () =>
        {
            rec = Store.LoadGame(gameId);
            if (rec.BeforeAfter is { } ba && change(rec, ba)) Store.SaveGame(gameId, rec);
        });
        return rec;
    }

    /// <summary>PollGames, every <see cref="PlayReadEvery"/> per game: an armed game not started within
    /// <see cref="NoStartAfter"/> gets the note (any later start still counts); a playing one, its play so far, read
    /// from the frame log (read-only, shared with the recorder) once its launch is stamped after ArmedAt.</summary>
    void BeforeAfterTick(IReadOnlyList<GameState> games, HashSet<string> running, DateTimeOffset at)
    {
        foreach (var s in games)
        {
            var id = s.Game.Id;
            if (s.BeforeAfter is not { Stage: BeforeArmed or AfterArmed or BeforePlaying or AfterPlaying } shown) continue;
            if (_pairPolled.TryGetValue(id, out var last) && at - last < PlayReadEvery) continue;
            _pairPolled[id] = at;
            var rec = PairChange(id, (r, ba) =>
            {
                if (ba.Stage is not (BeforeArmed or AfterArmed) || ba.ClearPending || running.Contains(id) || ba.Problem == NoStartNote || at - ba.ArmedAt < NoStartAfter) return false;
                ba.Problem = NoStartNote;
                return true;
            });
            if (rec?.BeforeAfter is not { } ba) continue;
            if (ba.Stage is BeforeArmed or AfterArmed)
            {
                if (ba.Problem == NoStartNote && shown.Problem != NoStartNote) ShowPair(id, shown with { Stage = ba.Stage, Problem = NoStartNote });
            }
            else if (running.Contains(id) && shown.Problem != FramesStoppedNote && FramesStopped(s.Game, ba))
                FramesStoppedWhilePlaying(s.Game, shown);
            else if (running.Contains(id) && FramesNow(s.Game, rec, ba.Minutes * 60_000.0) is { } r && r.LaunchUnixMs > ba.ArmedAt.ToUnixTimeMilliseconds()
                     && r.PlaySeconds != shown.PlayedSeconds)
            {
                _played[id] = r.PlaySeconds;
                ShowPair(id, shown with { PlayedSeconds = r.PlaySeconds });
            }
        }
    }

    /// <summary>The run's launch wrote <c>#frames,stopped</c>: frame generation made its swap chain and the recorder has no
    /// hook on the game's own frames, so no frame times follow and the play time never comes.</summary>
    bool FramesStopped(Game g, BeforeAfterRecord ba)
    {
        var csv = Path.Combine(SessionDir(g), "scskiller_creates.csv");
        var from = ba.ArmedAt.ToUnixTimeMilliseconds();
        return File.Exists(csv) && SessionLog.Launches(csv, SessionLog.LastSessionAt(csv)).Any(l => l.FramesStopped && l.Start > from);
    }

    /// <summary>Said at once, as OldCacheWhilePlaying, so the player can close the game rather than wait for play time that
    /// never comes. The run ends with the note at the game's exit.</summary>
    void FramesStoppedWhilePlaying(Game g, BeforeAfterInfo shown)
    {
        bool changed = false;
        PairChange(g.Id, (r, ba) =>
        {
            if (ba.Stage is not (BeforePlaying or AfterPlaying) || ba.Problem == FramesStoppedNote) return false;
            ba.Problem = FramesStoppedNote;
            return changed = true;
        });
        if (!changed) return;
        Log?.Report($"{g.Name}: frame times stopped when frame generation started, the before and after run can't be measured");
        ShowPair(g.Id, shown with { Problem = FramesStoppedNote });
    }

    void ShowPair(string gameId, BeforeAfterInfo info)
    {
        GameState? s = null;
        lock (_lock)
            if (_games.FindIndex(x => x.Game.Id == gameId) is var i and >= 0) _games[i] = s = _games[i] with { BeforeAfter = info };
        if (s != null) GameChanged?.Invoke(s);
    }

    /// <summary>After the exit's Refresh (which wrote the run's snapshot), or a run the app was closed for (not
    /// <paramref name="watched"/>). The run's driver and build are its snapshot's. Never throws: it runs in the watcher.</summary>
    void BeforeAfterExited(Game g, bool watched = true)
    {
        try
        {
            // the keys the before run's game held, learned on another task: its last sample may still be saving them
            bool sampled = true;   // the run's keys are all in: a sample that didn't end or failed may have missed one
            if (_learning.TryRemove(g.Id, out var learning))
                try { sampled = learning.Wait(LearnWait); }
                catch (AggregateException) { sampled = false; }
            var rec = Store.LoadGame(g.Id);
            if (rec.BeforeAfter is not { ClearPending: false, Stage: BeforeArmed or BeforePlaying or AfterArmed or AfterPlaying } ba) return;
            _played.TryRemove(g.Id, out _);
            var before = ba.Stage is BeforeArmed or BeforePlaying;
            var (run, problem) = RunOf(g, rec, ba);
            if (problem == null && FramesStopped(g, ba)) problem = FramesStoppedNote;
            problem ??= BeforeAfter.RunProblem(run, before);
            // saves a key it learned (only from a run that counts: one that didn't may not have made its cache): the clears below need it
            var restoreFailed = before ? EndSetAside(g, rec, ba, learn: run != null && problem == null, watched && sampled ? run?.LaunchUnixMs : null) : null;
            if (before && run != null && OldCache(rec, ba)) problem = OldCacheRedoNote;   // whatever else: the next clear needs the keys it now knows
            else if (before && run != null && (!watched || !sampled) && ba.ClearGap != false) problem ??= UnseenRedoNote;   // nothing saw which keys it held
            var (driver, build) = (run?.DriverId ?? DriverId, run?.Build ?? BuildNow(g));
            if (!before)
            {
                if ((IdentityChange(ba, driver, build) ?? Voided(g, ba)) is { } voided)
                    (ba.Stage, ba.Problem) = (AfterReady, voided);
                else if (problem != null)
                {
                    Recompile(g, rec, problem);
                    return;
                }
                else
                {
                    Snapshots.Pin(Store, g.Id, run!.LaunchUnixMs, "after");
                    (ba.After, ba.Stage, ba.Problem) = (run.LaunchUnixMs, Done, null);
                }
                Changed(g, rec);
                return;
            }
            // watched from start to exit, the snapshot was read right after the run: an update since the click is the
            // run's own; otherwise one may have come after the run, so it's played again
            if (problem == null && !watched && IdentityChange(ba, driver, build) is { } changed)
                problem = changed == DriverVoidNote ? DriverRedoNote : BuildRedoNote;
            (ba.DriverId, ba.Build) = (driver, build);
            if (problem != null)
            {
                if (restoreFailed != null) Log?.Report($"{g.Name}: the before run's cache: {restoreFailed}");
                Rearm(g, rec, problem);
                return;
            }
            Snapshots.Pin(Store, g.Id, run!.LaunchUnixMs, "before");
            ba.Before = run.LaunchUnixMs;
            if (restoreFailed == AsideHeldNote)
            {
                // a compile now would warm the run's folder: it starts once the set-aside one is back (the poll, or Continue)
                (ba.Stage, ba.Dirty, ba.ArmedAt, ba.Problem) = (BeforeAfterStage.Compiling, false, Clock(), AsideHeldNote);
                Changed(g, rec);
                return;
            }
            Recompile(g, rec, restoreFailed);
        }
        catch (Exception e) { Log?.Report($"{g.Name}: before and after recording: {e.Message}"); }
    }

    /// <summary>The driver-cache keys with files after the before run's clear: the game's keys not learned yet may be among
    /// them (DriverKeys' gap). Null without a driver cache to read.</summary>
    HashSet<string>? Survivors() => AppCache?.AllKeys().ToHashSet(StringComparer.OrdinalIgnoreCase);   // a failure is the clear's

    readonly object _setAsideLock = new();

    // the game is seen running up to a poll after it starts, and the launch stamp is taken at its first hooked device
    static readonly TimeSpan LaunchFileMargin = TimeSpan.FromSeconds(30);

    IBeforeRunAside? SetAside()
    {
        IBeforeRunAside? linux = null;
        LinuxAside(ref linux);
        return linux ?? AppCache switch
        {
            AmdAppCache amd => new DxQuarantine(amd.DxDir, Path.Combine(Store.DataDir, "dxcache-set-aside.json")),
            // its own journal: one of another GPU's files waits for that GPU's driver, which alone uses them
            NvidiaAppCache nv => new DxQuarantine(nv.Dir, Path.Combine(Store.DataDir, "nvidia-dxcache-set-aside.json"), nvidia: true),
            _ => null,
        };
    }

    partial void LinuxAside(ref IBeforeRunAside? aside);

    partial void SetAsideSteam(string gameId);

    partial void OtherAsides(List<IBeforeRunAside> asides);

    partial void UnlistedRuns(string gameId, List<string> roots, ref bool runs);

    partial void NotColdNote(string gameId, ref string? note);

    string GlJournal => Path.Combine(Store.DataDir, "nvidia-glcache-set-aside.json");

    /// <summary>Steam's folder of the game's app where Steam's shader pre-caching points this vendor's Vulkan cache
    /// (<see cref="VulkanPlan.SteamCache"/>: NVIDIA's GLCache, AMD's AMDv1): the driver reads it for that app alone. Null outside
    /// a Steam library, or for a vendor Steam doesn't point there.</summary>
    string? SteamGlCache(Game g, GpuVendor? vendor = null) => VulkanPlan.SteamCache(vendor ?? OwnDriver) is (_, { } tail) && MesaLinux.SteamApps(g.InstallDir) is { } apps && SteamSource.AppId(g.Id) is { } app
        ? Path.Combine([apps, "shadercache", app.ToString(System.Globalization.CultureInfo.InvariantCulture), .. tail]) : null;

    /// <summary>The GLCache set-aside in progress, its folder from the journal: only Steam's GLCache folder of the journal's
    /// game, the one of its install while it is listed. A journal naming any other folder is never acted on, and keeps every
    /// later set-aside out.</summary>
    DxQuarantine? GlSetAside()
    {
        if (!OperatingSystem.IsWindows() || DxQuarantine.Read(GlJournal) is not { Dir: { } dir } j || SteamSource.AppId(j.GameId) is not { } app) return null;
        // either vendor's folder: one set aside before the GPU changed is still put back
        GpuVendor[] vendors = [GpuVendor.Nvidia, GpuVendor.Amd];
        if (Games.FirstOrDefault(s => s.Game.Id == j.GameId) is { } listed && !vendors.Any(v => SteamGlCache(listed.Game, v) is { } own && SamePath(own, dir))) return null;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var parts = full.Split(Path.DirectorySeparatorChar);
        foreach (var v in vendors)
            if (VulkanPlan.SteamCache(v) is (_, { } leaf) && (string[])["steamapps", "shadercache", app.ToString(System.Globalization.CultureInfo.InvariantCulture), .. leaf] is var tail
                && parts.Length > tail.Length && parts[^tail.Length..].SequenceEqual(tail, StringComparer.OrdinalIgnoreCase)) return GlQuarantine(full);
        return null;
    }

    DxQuarantine GlQuarantine(string dir) => new(dir, GlJournal, glCache: true) { Busy = SteamReplaying };

    /// <summary>A Steam Vulkan game on Windows: Steam's GLCache folder of its app set aside for the before run. Cold when all of
    /// it was and the game's last session ran with its cache there. The driver's own Vulkan cache is the clear's, by the keys
    /// learned for the game (<see cref="AppCacheFiles.VulkanPrefix"/>). Nothing moves while a file is
    /// held open or Steam replays the game's shaders.</summary>
    bool SetAsideVulkan(GameState s, GameRecord rec)
    {
        if (SteamGlCache(s.Game) is not { } dir) return false;
        lock (_setAsideLock)
        {
            var done = GlQuarantine(dir).SetAside(s.Game.Id, _ => false, held: SteamReplaying());
            Log?.Report($"{s.Game.Name}: {done.Moves.Count} files of Steam's Vulkan cache set aside for the before run" + (done.Skipped.Count > 0 ? ", none moved: in use, Steam processing shaders, or another set-aside not put back yet" : ""));
            var nv = dir;   // the folder the vendor's variable names: the first of its tail
            for (var up = VulkanPlan.SteamCache(Vendor.Vendor)!.Value.Tail.Length; up > 1; up--) nv = Path.GetDirectoryName(nv)!;
            var redirected = VulkanPlan.CachePath(rec.VulkanCacheEnv, Vendor.Vendor) is { } path && (SamePath(path, nv) || SamePath(path, dir));
            if (!redirected) Log?.Report($"{s.Game.Name}: its last launch's Vulkan cache wasn't in {nv}, so the before run may not be cold");
            return done.Skipped.Count == 0 && redirected;
        }
    }

    static bool SamePath(string a, string b)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>The report's line for a before run that read a Vulkan cache SCSKiller couldn't set aside.</summary>
    string? VulkanNotColdReport(string gameId)
    {
        string? note = null;
        NotColdNote(gameId, ref note);
        return note == null ? null : BeforeAfter.VulkanNotCold;
    }

    /// <summary>The before run's clear (the game's own caches too). With a key of the game not known (on AMD, a DirectX 11
    /// game's DxCache key, on NVIDIA any), every driver cache file of no known game is set aside (DxQuarantine) until the
    /// run ends, so the run is cold there too; skipped while another game's run has files set aside. A file left in use
    /// stays among the survivors, so a game seen holding it plays again (OldCache). The keys left with files, and whether
    /// the clear may have missed some of the game's.</summary>
    (HashSet<string>? Survivors, bool Gap) ClearBeforeRun(string gameId)
    {
        SetAsideSteam(gameId);   // refused before anything is cleared
        // the last run's GLCache files still aside: a set-aside over the run's files would make this one warm
        if (OperatingSystem.IsWindows() && PutBackGl(gameId) is { } waits) throw new InvalidOperationException(waits.Length > 0 ? waits : AsideHeldNote);
        ClearCache(gameId, gamePrecache: true);
        var s = Find(gameId);
        var rec = Store.LoadGame(gameId);
        bool gap = ClearGap(s, rec);
        // its cache is Vulkan's alone: Steam's folder set aside, or outside Steam its Vulkan keys' files, which the clear deleted
        if (OperatingSystem.IsWindows() && VulkanRoute(s.Game, s.Engine)) return SteamGlCache(s.Game) == null ? (Survivors(), gap) : ([], !SetAsideVulkan(s, rec));
        if (gap && (Dx11Only(s) || AppCache is NvidiaAppCache) && SetAside() is DxQuarantine q)
            lock (_setAsideLock)
            {
                if (q.Pending is { } j && j.GameId == gameId && q.Restore() is { Count: > 0 } left) throw new IOException(F(RestoreFailedNote, left.Count, q.Folder));
                if (q.Pending == null)
                {
                    var known = Games.Where(o => o.Game.Id != gameId).SelectMany(o => Store.LoadGame(o.Game.Id).CacheKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var done = q.SetAside(gameId, known.Contains);
                    Log?.Report($"{s.Game.Name}: {done.Moves.Count} driver cache files of other apps set aside for the before run"
                        + (done.Skipped.Count > 0 ? $", {done.Skipped.Count} left in use" : ""));
                    // a run nothing saw may have used a file left, or a same-named game's key, which is kept
                    var exe = Path.GetFileName(s.Game.ExePath);
                    gap = AppCache is NvidiaAppCache && (done.Skipped.Count > 0
                        || Games.Any(o => o.Game.Id != gameId && Path.GetFileName(o.Game.ExePath).Equals(exe, StringComparison.OrdinalIgnoreCase)));
                }
            }
        return (Survivors(), gap);
    }

    /// <summary>One of the files is open in a process, or that can't be told (ProcessesUsing finds none when it can't ask): all
    /// are opened exclusively at once.</summary>
    static bool Held(IReadOnlyList<FileInfo> files)
    {
        if (files.Any(f => AppCacheFiles.ProcessesUsing(f.FullName).Count > 0)) return true;
        var open = new List<FileStream>();
        try
        {
            foreach (var f in files) open.Add(new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.None));
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
        finally { foreach (var h in open) h.Dispose(); }
    }

    static bool Dx11Only(GameState s) => s.Engine is { } e && Dx11Only(e);
    internal static bool Dx11Only(EngineInfo e) => e.GraphicsApi.StartsWith("D3D11") && !e.GraphicsApi.Contains("D3D12");

    static string F(string format, params object[] args) => string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args);

    /// <summary>The before run's end: its GLCache set-aside goes back (one still waiting holds the compile: AsideHeldNote, and
    /// the poll puts it back), then the DxCache one.</summary>
    string? EndSetAside(Game g, GameRecord rec, BeforeAfterRecord ba, bool learn, long? launchUnixMs)
    {
        var gl = PutBackGl(g.Id);
        var dx = EndDxSetAside(g, rec, ba, learn, launchUnixMs);
        return gl is { Length: > 0 } ? gl : dx ?? (gl == "" ? AsideHeldNote : null);
    }

    /// <summary>The before run's end, with DxCache files set aside for it: the DxCache key made during the run is the game's
    /// (learned; its set-aside files, the old cache, are deleted) when the game's own process was seen holding it, or else
    /// when it is the only key made since the game started, held by no process, no other game has the exe name and the game
    /// didn't run D3D12. Any other key made: DxShared, nothing learned. The rest goes back, and everything while another game
    /// has the exe name. On NVIDIA everything goes back: the clear that follows deletes the keys the game was seen holding.
    /// The line to show when a file couldn't be put back.</summary>
    string? EndDxSetAside(Game g, GameRecord rec, BeforeAfterRecord ba, bool learn, long? launchUnixMs)
    {
        if (SetAside() is not { } q) return null;
        lock (_setAsideLock)
        {
            if (q.PendingGame != g.Id) return null;
            string? learned = null;
            var exe = Path.GetFileName(g.ExePath);
            // a same-named game's cache is the same files: never deleted here, as Clear cache refuses them
            var twin = Games.Any(o => o.Game.Id != g.Id && Path.GetFileName(o.Game.ExePath).Equals(exe, StringComparison.OrdinalIgnoreCase));
            if (AppCache is AmdAppCache amd && q is DxQuarantine { Pending: { } j })
                try
                {
                    var now = Survivors() ?? [];
                    bool New(string k) => ba.Survivors?.Contains(k) != true && now.Contains(k);
                    var made = now.Where(k => k.StartsWith(AmdAppCache.D3D11Prefix, StringComparison.Ordinal) && New(k)).ToList();
                    // a key is its exe name's: one the game's own process was seen holding is the game's
                    var own = made.Where(rec.GameKeys.Contains).ToList();
                    // else the one made since the launch, unless another app may have made it while the game made none: its key kept
                    // as a same-named game's, the game running D3D12, or another process holding it
                    // null: the run wasn't watched or sampled to the end, so a D3D12 key the game held may be missing from GameKeys
                    var since = launchUnixMs is { } ms && DateTimeOffset.FromUnixTimeMilliseconds(ms) is var launch
                        ? (rec.LastPlay is { } w && w.From <= launch && launch <= w.To ? w.From : launch).UtcDateTime - LaunchFileMargin : DateTime.MaxValue;
                    var files = made.ToDictionary(k => k, k => amd.FilesOf([k]));
                    // NTFS tunneling gives a file made again soon after its set-aside namesake left that file's creation time
                    bool Tunneled(FileInfo f) => j.Moves.Any(m => m.From.Equals(f.FullName, StringComparison.OrdinalIgnoreCase) && File.Exists(m.To) && FileTimes.CreatedUtc(m.To) is var was && (was == null || was == FileTimes.CreatedUtc(f)));
                    // a file not known when made may be from before the launch or one of several keys made since: nothing is learned
                    if (own.Count == 0 && !twin && made.All(k => files[k].Count > 0 && files[k].All(f => FileTimes.CreatedUtc(f) != null) && !files[k].Any(Tunneled))
                        && made.Where(k => files[k].All(f => FileTimes.CreatedUtc(f) >= since)).ToList() is [var only]
                        && !now.Any(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.Ordinal) && New(k)
                            && (rec.GameKeys.Contains(k) || k == AmdAppCache.HintKey(WarmExeName(g, rec))))   // the hint: a sample may have missed it
                        && !Held(files[only]))
                        own = [only];
                    ba.DxShared = learn && made.Count > 0 && own.Count != 1;
                    if (learn && own.Count == 1)
                    {
                        (rec.CacheKeys, rec.GameKeys) = ([.. rec.CacheKeys, own[0]], [.. rec.GameKeys, own[0]]);
                        StampDriver(rec, own);
                        Store.SaveGame(g.Id, rec);
                        learned = own[0];
                        Log?.Report($"{g.Name}: its DirectX 11 cache key {learned} learned from the before run");
                    }
                    else if (ba.DxShared) Log?.Report($"{g.Name}: {made.Count} DirectX 11 cache keys made during the before run, none taken as the game's");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: the DirectX 11 cache key wasn't learned: {e.Message}"); }
            var left = q.Restore(twin ? null : learned);
            if (left.Count == 0) return q.AfterRunReads && q.PendingGame == g.Id ? AsideHeldNote : null;   // a put-back that waits: still aside
            Log?.Report($"{g.Name}: {left.Count} driver cache files couldn't be put back from {q.Folder}");
            return F(RestoreFailedNote, left.Count, q.Folder);
        }
    }

    /// <summary>PollGames: files set aside for a run that won't end through BeforeAfterExited (cancelled, started over, the
    /// game removed, the app stopped while nothing was armed) go back. <paramref name="resume"/>: a pair whose put-back
    /// waited then goes on (<see cref="ResumeAfterPutBack"/>).</summary>
    void PutBackSetAside(bool resume = true)
    {
        List<IBeforeRunAside> others = [];
        OtherAsides(others);
        foreach (var o in others)   // no run under this driver reads it: back whatever the pair's stage
            lock (_setAsideLock)
                if (o.PendingGame is { } other && !AsideGameRuns(other)) TryPutBack(o, other);
        string? waiting = null;
        var reads = false;
        foreach (var q in PairAsides())
        {
            lock (_setAsideLock)
                if (q.PendingGame is { } id)
                {
                    var game = Games.FirstOrDefault(s => s.Game.Id == id);
                    // before the first scan lists the games, the record tells an armed run's set-aside from one to put back
                    var ba = game != null || Volatile.Read(ref _listed) == 0 ? Store.LoadGame(id).BeforeAfter : null;
                    if (ba is not { Stage: BeforeArmed, ClearPending: false } && !AsideGameRuns(id) && TryPutBack(q, id) is { } note && game != null && ba != null)
                        ShowPair(game.Game.Id, new(ba.Stage, ba.Minutes, note, _played.GetValueOrDefault(game.Game.Id)));
                }
            if (!q.AfterRunReads) continue;
            reads = true;
            waiting ??= q.PendingGame;
        }
        if (resume && reads) ResumeAfterPutBack(waiting);
    }

    /// <summary>The set-asides a pair's before run makes under this driver: the driver cache's (Steam's folder on Linux) and on
    /// Windows the GLCache one of a Vulkan game.</summary>
    IEnumerable<IBeforeRunAside> PairAsides() => new IBeforeRunAside?[] { SetAside(), GlSetAside() }.OfType<IBeforeRunAside>();

    /// <summary>The GLCache set-aside of <paramref name="gameId"/> put back: null when done or there was none, "" while the
    /// game runs, its files are held or Steam replays or merges, else the line to show for the files that couldn't be.</summary>
    string? PutBackGl(string gameId)
    {
        lock (_setAsideLock)
        {
            if (GlSetAside() is not { PendingGame: { } id } q || id != gameId) return null;
            if (AsideGameRuns(gameId)) return "";
            return TryPutBack(q, gameId) ?? (q.PendingGame == null ? null : "");
        }
    }

    /// <summary>The game a set-aside is pending for runs, so its cache isn't swapped under it: a listed game as
    /// <see cref="GameRunning(Game)"/> sees it, an unlisted one (before the first scan lists the games, or removed) while a
    /// process runs from its recorded install folder or <see cref="UnlistedRuns"/> says so.</summary>
    bool AsideGameRuns(string id)
    {
        if (IsPlaying(id)) return true;
        if (Games.FirstOrDefault(s => s.Game.Id == id) is { } listed) return GameRunning(listed.Game);
        var rec = Store.LoadGame(id);
        List<string> roots = [.. new[] { rec.RecorderInstallDir, Path.GetDirectoryName(rec.RecorderExe) }.OfType<string>()];
        var runs = false;
        UnlistedRuns(id, roots, ref runs);
        return runs || roots.Count > 0 && Processes(true).Any(p => ProcessPath(p.Pid) is { } path
            && roots.Any(r => GameFiles.Inside(r, path) || GameFiles.FinalPath(r) is { } real && GameFiles.Inside(real, path)));
    }

    readonly ConcurrentDictionary<string, string> _putBackLeft = new();   // game id -> why the last put-back of its set-aside left files

    /// <summary>One put-back of what <paramref name="q"/> holds for <paramref name="id"/>: null, or why files couldn't go back.</summary>
    string? TryPutBack(IBeforeRunAside q, string id)
    {
        if (q.Restore() is not { Count: > 0 } left)
        {
            _putBackLeft.TryRemove(id, out _);
            return null;
        }
        Log?.Report($"{left.Count} driver cache files couldn't be put back from {q.Folder}");
        return _putBackLeft[id] = F(RestoreFailedNote, left.Count, q.Folder);
    }

    /// <summary>Waits until every cache a before run set aside is back (this driver's, and on Linux the other driver's), as
    /// the poll puts them back: it polls itself while nothing else does, every <see cref="WatchInterval"/>, and reports why
    /// it waits to <paramref name="status"/> when that changes. An armed before run's cache stays aside, as its run reads it.
    /// Returns null once nothing is set aside, or the armed game's id. Cancelled: OperationCanceledException between polls,
    /// never during a put-back, and the journal keeps what is still aside for the next poll or start.</summary>
    public async Task<string?> PutBackSetAsideAsync(IProgress<string>? status, CancellationToken ct)
    {
        string? said = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // the watcher's poll does it while it runs
            if (Environment.TickCount64 - Volatile.Read(ref _polledAt) > 2 * WatchInterval.TotalMilliseconds)
                try { await Task.Run(PollGames, CancellationToken.None); }
                catch (Exception e) { Log?.Report($"putting back set-aside caches: {e.Message}"); }
            var (armed, why) = SetAsideWait();
            if (why == null) return armed;
            if (why != said) status?.Report(said = why);
            await Task.Delay(WatchInterval, ct);
        }
    }

    /// <summary>Why a set-aside cache isn't back (null: none is aside but an armed before run's), and that armed game.</summary>
    (string? Armed, string? Why) SetAsideWait()
    {
        List<IBeforeRunAside> others = [];
        OtherAsides(others);
        var own = PairAsides().ToList();
        string? armed = null;
        foreach (var q in own.Concat(others))
        {
            if (q.PendingGame is not { } id) continue;
            var game = Games.FirstOrDefault(s => s.Game.Id == id);
            if (own.Contains(q) && (game != null || Volatile.Read(ref _listed) == 0) && Store.LoadGame(id).BeforeAfter is { Stage: BeforeArmed, ClearPending: false })
            {
                armed = id;
                continue;
            }
            var name = game?.Game.Name ?? id;
            return (armed, AsideGameRuns(id) ? F(PutBackRunningNote, name) : _putBackLeft.TryGetValue(id, out var note) ? note : F(PutBackInUseNote, name));
        }
        return (armed, null);
    }

    /// <summary>What is still set aside, a game's folder then each file set aside in it (DxQuarantine's journal names them), for
    /// a caller that exits before it is back: the journal stays, and the next start's poll puts it back.</summary>
    public List<(string Game, string Folder, List<string> Files)> SetAsideLeft()
    {
        List<IBeforeRunAside> all = [.. PairAsides()];
        OtherAsides(all);
        return [.. all.Where(q => q.PendingGame != null).Select(q => (q.PendingGame!, q.Folder,
            q is DxQuarantine { Pending: { } j } ? j.Moves.Select(m => m.To).Where(File.Exists).ToList() : new List<string>()))];
    }

    /// <summary>A pair whose put-back waited goes on once nothing of its game is aside (<paramref name="pending"/>: the game
    /// whose is), as Continue would: a counted before run compiles, a rearm whose clear met the wait clears. A voided pair, a
    /// running game or a compile in the queue keeps the note and its button.</summary>
    void ResumeAfterPutBack(string? pending)
    {
        foreach (var s in Games.Where(s => s.Game.Id != pending
                     && s.BeforeAfter is { Stage: BeforeAfterStage.Compiling, Problem: AsideHeldNote } or { Stage: BeforeArmed, Problem: ClearHeldNote }))
        {
            var g = s.Game;
            if (IsPlaying(g.Id) || GameRunning(g) || QueueItemOf(g.Id) is { } queued && !Finished(queued.Stage)) continue;
            var rec = Store.LoadGame(g.Id);
            switch (rec.BeforeAfter)
            {
                case { Stage: BeforeAfterStage.Compiling, Problem: AsideHeldNote } ba when Voided(g, ba) == null:
                    Log?.Report($"{g.Name}: the before run's cache is back, its compile starts");
                    Recompile(g, rec, null);
                    break;
                // its clear would refuse while another game's cache is aside
                case { Stage: BeforeArmed, ClearPending: true, Problem: ClearHeldNote } ba when pending == null:
                    Log?.Report($"{g.Name}: the before run's cache is back, it's cleared again");
                    lock (_setAsideLock)
                    {
                        try
                        {
                            var (survivors, gap) = ClearBeforeRun(g.Id);
                            string? note = null;
                            NotColdNote(g.Id, ref note);
                            (ba.ArmedAt, ba.ClearPending, ba.Problem, ba.Survivors, ba.ClearGap, ba.DxShared) = (Clock(), false, note, survivors, gap, false);
                        }
                        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { ba.Problem = $"Couldn't clear the cache: {e.Message}"; }
                        Changed(g, rec);
                    }
                    break;
            }
        }
    }

    /// <summary>The clear doesn't know every key of the game: DriverKeys' gap, or on AMD a DirectX 11 game without a
    /// DxCache key learned (AMD's D3D11 driver doesn't always hold that file open, so the run can't show it either).</summary>
    bool ClearGap(GameState s, GameRecord rec) =>
        AppCache is AmdAppCache && s.Engine?.GraphicsApi is { } api && api.StartsWith("D3D11") && !api.Contains("D3D12")
            ? !rec.CacheKeys.Any(k => k.StartsWith(AmdAppCache.D3D11Prefix, StringComparison.Ordinal))
            : DriverKeys(s.Game, rec).Gap != null;

    /// <summary>How long a before run's exit waits for the game's last key sample: measured 6-10 s on a 42 GB cache.</summary>
    static readonly TimeSpan LearnWait = TimeSpan.FromSeconds(60);

    /// <summary>The before run's game holds open driver-cache files of a key that had files right after the clear: it started
    /// with an old cache (the drivers keep a running app's files open, so a key the game never held wasn't used).</summary>
    bool OldCache(GameRecord rec, BeforeAfterRecord ba) =>
        ba.Survivors is { } old ? rec.GameKeys.Any(k => old.Contains(k, StringComparer.OrdinalIgnoreCase)) : AppCache != null;   // none recorded: an older build's pair

    /// <summary>LearnWhilePlaying: the before run's game holds a key that survived the clear, so the run won't count. Said at
    /// once, so the player can close the game rather than finish it.</summary>
    void OldCacheWhilePlaying(Game g)
    {
        BeforeAfterInfo? shown = null;
        PairChange(g.Id, (r, ba) =>
        {
            if (ba is not { Stage: BeforePlaying } || ba.Problem == OldCacheNote || !OldCache(r, ba)) return false;
            ba.Problem = OldCacheNote;
            shown = new(ba.Stage, ba.Minutes, OldCacheNote, _played.GetValueOrDefault(g.Id));
            return true;
        });
        if (shown != null)
        {
            Log?.Report($"{g.Name}: the before run uses driver cache files from before its clear: it's played again");
            ShowPair(g.Id, shown);
        }
    }

    /// <summary>The before run again: armed only once the cache is cleared, else <see cref="BeforeAfterRecord.ClearPending"/>
    /// until ContinueBeforeAfter's clear succeeds.</summary>
    void Rearm(Game g, GameRecord rec, string problem)
    {
        var ba = rec.BeforeAfter!;
        ba.Stage = BeforeArmed;
        lock (_setAsideLock)   // a poll puts back files set aside for a run that isn't armed
        {
            try
            {
                var (survivors, gap) = ClearBeforeRun(g.Id);
                (ba.ArmedAt, ba.ClearPending, ba.Problem, ba.Survivors, ba.ClearGap, ba.DxShared) = (Clock(), false, problem, survivors, gap, false);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                (ba.ClearPending, ba.Problem) = (true, $"Couldn't clear the cache: {e.Message}");
            }
            Changed(g, rec);
        }
    }

    /// <summary>After a scan, for what the app was closed for: a run ends here (a playing stage, or an armed one with a launch
    /// since), a launch after the compile was asked for makes the pair Dirty, and a listed state whose stage is behind the
    /// record's is evaluated again.</summary>
    void BeforeAfterCatchUp(IEnumerable<GameState> states)
    {
        foreach (var s in states)
            try
            {
                var rec = Store.LoadGame(s.Game.Id);
                var running = IsPlaying(s.Game.Id) || GameRunning(s.Game);
                switch (rec.BeforeAfter)
                {
                    case { ClearPending: false, Stage: BeforePlaying or AfterPlaying } when !running:
                    case { ClearPending: false, Stage: BeforeArmed or AfterArmed } ba when !running && LaunchesAfter(s.Game, rec, ba.ArmedAt).After.Count > 0:
                        Refresh(s.Game);   // a start that reuses the last list read nothing: the run's snapshot is written here
                        BeforeAfterExited(s.Game, watched: false);
                        break;
                    case { Stage: BeforeAfterStage.Compiling or AfterReady, Dirty: false } ba when LaunchesAfter(s.Game, rec, ba.ArmedAt).After.Count > 0:
                        PairChange(s.Game.Id, (_, now) => now.Stage is BeforeAfterStage.Compiling or AfterReady && (now.Dirty = true));
                        Refresh(s.Game);
                        break;
                    case var ba when ba?.Stage != s.BeforeAfter?.Stage:
                        Refresh(s.Game);
                        break;
                }
            }
            catch (Exception e) { Log?.Report($"{s.Game.Name}: before and after recording: {e.Message}"); }
    }

    /// <summary>A finished queue item: a pair waiting for the game's compile moves on (<see cref="BeforeAfterNow"/>).</summary>
    void BeforeAfterQueued(QueueItem item)
    {
        if (!Finished(item.Stage)) return;
        try
        {
            if (Store.LoadGame(item.GameId).BeforeAfter is { Stage: BeforeAfterStage.Compiling } && Games.FirstOrDefault(s => s.Game.Id == item.GameId) is { } s) Refresh(s.Game);
        }
        catch (Exception e) { Log?.Report($"{item.GameId}: before and after recording: {e.Message}"); }   // never turns a finished compile into a failed one
    }

    /// <summary>The evaluation's view of the pair: Compiling moves to AfterReady once this compile is done and the game
    /// shows Warmed, and the note says what holds it.</summary>
    GameState BeforeAfterNow(GameState s, GameRecord evaluated)
    {
        if (evaluated.BeforeAfter == null) return s;
        GameRecord? rec;
        var moved = false;
        try
        {
            // newer than the evaluation's copy when the exit just wrote it
            rec = PairChange(s.Game.Id, (r, ba) =>
            {
                if (ba.Stage != BeforeAfterStage.Compiling || CompileEnded(s, r, ba) is var now && now == (ba.Stage, ba.Problem)) return false;
                (ba.Stage, ba.Problem) = now;
                return moved = true;
            });
            if (moved) UpdateRecorderIni(s.Game, rec!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log?.Report($"{s.Game.Name}: couldn't save its record: {e.Message}");
            rec = Store.LoadGame(s.Game.Id);
        }
        if (rec?.BeforeAfter is not { } ba) return s;
        var problem = ba.Stage is not (BeforeArmed or BeforePlaying or Done) && Voided(s.Game, ba) is { } voided ? voided
            : ba.Problem
              ?? (ba.Stage == AfterReady && ba.Dirty ? DirtyNote : null)
              ?? (ba.Stage == AfterReady && s.Status != GameStatus.Warmed ? s.StatusReason : null)
              ?? (ba.Stage is BeforeArmed or BeforePlaying && s.RecordingPaused ? PausedNote(Settings) : null);
        return s with { BeforeAfter = new(ba.Stage, ba.Minutes, problem, _played.GetValueOrDefault(s.Game.Id), ba.ClearPending) };
    }

    /// <summary>A Done item is the compile Recompile queued (Add replaces a finished one); without one (the app restarted)
    /// a warm since the compile was asked for.</summary>
    (BeforeAfterStage, string?) CompileEnded(GameState s, GameRecord rec, BeforeAfterRecord ba) => QueueItemOf(s.Game.Id) switch
    {
        { Stage: QueueStage.Failed or QueueStage.Stopped } q => (BeforeAfterStage.Compiling, q.Error ?? StoppedNote),
        { Stage: not QueueStage.Done } => (BeforeAfterStage.Compiling, ba.Problem),
        var q when s.Status == GameStatus.Warmed && (q != null || rec.WarmedAt >= ba.ArmedAt) => (AfterReady, null),
        _ => (BeforeAfterStage.Compiling, s.Status == GameStatus.Stale ? s.StatusReason : ba.Problem ?? NotCompiledNote),
    };
}
