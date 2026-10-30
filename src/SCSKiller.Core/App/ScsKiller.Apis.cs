using System.Text.Json;
using System.Text.Json.Nodes;

namespace SCSKiller.Core.App;

public sealed partial class ScsKiller
{
    /// <summary>The <see cref="GameRecord"/> fields of one graphics API's compile: its index, plan and last complete warm.
    /// What the game, its recorder and the user's settings are stays shared.</summary>
    static readonly string[] ApiFields =
    [
        nameof(GameRecord.IndexContentHash), nameof(GameRecord.IndexLegacyHashes), nameof(GameRecord.IndexGameVersion), nameof(GameRecord.IndexExeStamp), nameof(GameRecord.ShaderCount),
        nameof(GameRecord.Plan), nameof(GameRecord.PlanBuiltAt), nameof(GameRecord.PlanVersion), nameof(GameRecord.WarmedPlanVersion), nameof(GameRecord.PlanCheckFailed),
        nameof(GameRecord.PlanReaderVersion), nameof(GameRecord.WarmedReaderVersion), nameof(GameRecord.PlanRules), nameof(GameRecord.WarmedPlanRules),
        nameof(GameRecord.IdleHeldBack), nameof(GameRecord.PlanItems), nameof(GameRecord.WarmedPlanItems), nameof(GameRecord.PlanKeysFile),
        nameof(GameRecord.WarmKeysFile), nameof(GameRecord.Pending), nameof(GameRecord.PlanPerStage), nameof(GameRecord.PlanMaximumCapped),
        nameof(GameRecord.PlanCacheBudget), nameof(GameRecord.PlanD3DSCacheCharged), nameof(GameRecord.WarmedPerStage), nameof(GameRecord.WarmedDelivery), nameof(GameRecord.PlanMiddleware), nameof(GameRecord.PlanCommunity),
        nameof(GameRecord.PlanOfficial), nameof(GameRecord.PlanSteam), nameof(GameRecord.PlanMaps), nameof(GameRecord.PlanEngine),
        nameof(GameRecord.PlanFiltering), nameof(GameRecord.PlanRayTracing), nameof(GameRecord.PlanCacheCapGB),
        nameof(GameRecord.WarmedD3DSCache), nameof(GameRecord.WarmedD3DSCacheBytes), nameof(GameRecord.WarmedD3DSCacheAt), nameof(GameRecord.D3DSCacheDamaged),
        nameof(GameRecord.D3DSCacheStamp), nameof(GameRecord.D3DSCacheStampAt), nameof(GameRecord.D3DSCacheStampFolder), nameof(GameRecord.D3DSCacheLaunch),
        nameof(GameRecord.D3DSCacheLaunchAt), nameof(GameRecord.D3DSCacheFoldKeys), nameof(GameRecord.HitCheck), nameof(GameRecord.HitCheckAt),
        nameof(GameRecord.HitCheckRun),
        nameof(GameRecord.WarmedDriverVersion), nameof(GameRecord.WarmedDriverId), nameof(GameRecord.WarmedAt), nameof(GameRecord.LastWarmTime),
        nameof(GameRecord.LastCacheGrowthBytes), nameof(GameRecord.LastWarmFailed), nameof(GameRecord.LastWarmSkipped), nameof(GameRecord.LastWarmNeedsRecording),
        nameof(GameRecord.LastWarmCrashed), nameof(GameRecord.LastWarmCrashedBefore), nameof(GameRecord.LastWarmDriverSkipped), nameof(GameRecord.LastWarmKnown), nameof(GameRecord.LastWarmUnbuilt), nameof(GameRecord.LastWarmInvalid),
        nameof(GameRecord.LastWarmUnsupported), nameof(GameRecord.LastWarmTried), nameof(GameRecord.LastWarmFailedBy), nameof(GameRecord.WarmedRoomLeft), nameof(GameRecord.WarmedIndexHash), nameof(GameRecord.WarmedExeStamp),
        nameof(GameRecord.WarmedGameVersion), nameof(GameRecord.WarmedLayer), nameof(GameRecord.WarmedIndexStamp), nameof(GameRecord.BytesPerPso),
        nameof(GameRecord.PsoPerSecond), nameof(GameRecord.PsoPerSecondDriver), nameof(GameRecord.WarmedKeys), nameof(GameRecord.WarmedFiles),
        nameof(GameRecord.WarmedFileBytes), nameof(GameRecord.WarmedFileUsed), nameof(GameRecord.WarmedFileIds), nameof(GameRecord.WarmedSteamParcId), nameof(GameRecord.WarmedSteamParcBytes), nameof(GameRecord.WarmedExeName),
        nameof(GameRecord.WarmedAftermath), nameof(GameRecord.AftermathRetry), nameof(GameRecord.AftermathPlainHits), nameof(GameRecord.AftermathLost),
        nameof(GameRecord.WarmedAgsApp), nameof(GameRecord.WarmedRayTracing), nameof(GameRecord.WarmedCareful), nameof(GameRecord.FirstLaunch),
        nameof(GameRecord.UnreachedWarm), nameof(GameRecord.UnreachedRecheckedAt), nameof(GameRecord.UnreachedLifted), nameof(GameRecord.CompileFailedBuild),
    ];

    static readonly JsonObject EmptyFields = JsonSerializer.SerializeToNode(new GameRecord(), AppStore.Json)!.AsObject();

    /// <summary>The game runs another graphics API than its record's compile is for (<see cref="GameRecord.PlanApi"/>): that
    /// compile goes aside under its API (<see cref="GameRecord.ApiCompiles"/>) with its plan.bin and compiled.keys, and the
    /// new API's comes back, else none. No driver cache is touched: the compile that comes back is judged by its own warm's
    /// files and driver, and one from before the game's last Clear cache comes back as not compiled. Read, moved and saved
    /// under the record's lock: the caller loads the record again when it returns true, and makes sure no compile of the
    /// game holds it.</summary>
    /// <param name="held">checked under the lock: a compile of the game holds the record, nothing switches</param>
    /// <param name="seen">the caller's copy: nothing to do on it skips the lock</param>
    bool SwitchApi(Game g, EngineInfo? engine, Func<bool>? held = null, GameRecord? seen = null)
    {
        if (ApiKey(engine?.GraphicsApi) is not { } now) return false;
        var dir = Store.GameDir(g.Id);
        if (seen != null && (seen.PlanApi == null || seen.PlanApi == now) && !File.Exists(Path.Combine(dir, $"compiled.{now}.keys"))) return false;
        var switched = false;
        AppStore.Locked(Path.Combine(dir, "state.json"), () =>
        {
            if (held?.Invoke() == true) return;
            var rec = Store.LoadGame(g.Id);
            var compiled = Path.Combine(dir, CompiledItems.File);
            var compiledBack = Path.Combine(dir, $"compiled.{now}.keys");
            // a switch stopped after its save: its compiled.keys still waits under the kept name
            if (rec.PlanApi == now && rec.ApiCompiles?.ContainsKey(now) != true && !File.Exists(compiled) && File.Exists(compiledBack)
                && rec.WarmedAt is { } warmed && !(rec.CacheClearedAt > warmed))
                File.Move(compiledBack, compiled);
            if (rec.PlanApi is not { } was || now == was) return;
            var back = rec.ApiCompiles?.GetValueOrDefault(now);
            // "11+12" and one of its APIs: the plan check counts what the other plan adds (OtherApi)
            if (back == null && (now.Contains(was) || was.Contains(now))) return;
            var slots = rec.ApiCompiles ?? [];
            slots.Remove(now);
            if (rec.Plan != null || rec.WarmedAt != null)
            {
                // moved before the save: a stop between them leaves the record without its plan file and compiled.keys, so a re-plan and a full compile
                var planAside = Path.Combine(dir, $"plan.{was}.bin");
                var movePlan = rec.Plan is { } p && File.Exists(p.FilePath) && !string.Equals(Path.GetFullPath(p.FilePath), planAside, StringComparison.OrdinalIgnoreCase);
                if (movePlan) File.Move(rec.Plan!.FilePath, planAside, true);
                try { if (File.Exists(compiled)) File.Move(compiled, Path.Combine(dir, $"compiled.{was}.keys"), true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (movePlan) File.Move(planAside, rec.Plan!.FilePath, true);
                    throw;
                }
                if (rec.Plan is { } kept && File.Exists(planAside)) rec.Plan = kept with { FilePath = planAside };
                var slot = new JsonObject();
                foreach (var f in ApiFields) slot[f] = JsonSerializer.SerializeToNode(Field(f).GetValue(rec), Field(f).PropertyType, AppStore.Json);
                slots[was] = slot;
            }
            foreach (var f in ApiFields)   // a field a kept compile lacks (added since): as a new record has it
                Field(f).SetValue(rec, (back != null && back.TryGetPropertyValue(f, out var v) ? v : EmptyFields[f])?.Deserialize(Field(f).PropertyType, AppStore.Json));
            if (rec.Plan is { } restored) rec.Plan = restored with { FilePath = Path.Combine(dir, Path.GetFileName(restored.FilePath)) };   // a moved data folder's
            var cleared = rec.WarmedAt is { } at && rec.CacheClearedAt > at;
            if (cleared) ResetWarm(rec);   // its files went with the game's Clear cache
            (rec.PlanApi, rec.ApiCompiles, rec.ResumeAt, rec.KeysPending) = (now, slots.Count > 0 ? slots : null, 0, true);   // the keys file names the index now in use
            Store.SaveGame(g.Id, rec);
            if (back != null && !cleared && File.Exists(compiledBack)) File.Move(compiledBack, compiled, true);
            PruneApiFiles(g.Id, rec);
            switched = true;
            Log?.Report($"{g.Name}: now on {KeyName(now)}: " + (back == null ? "not compiled for it yet" : cleared ? "its compile for it went with a Clear cache" : "its compile for it is back")
                + (slots.ContainsKey(was) ? $", the {KeyName(was)} compile is kept" : ""));
        });
        return switched;
    }

    static System.Reflection.PropertyInfo Field(string name) => typeof(GameRecord).GetProperty(name)!;

    /// <summary>plan.&lt;api&gt;.bin and compiled.&lt;api&gt;.keys files no compile of the record names: a plan rebuilt since
    /// went to plan.bin, a compile dropped.</summary>
    void PruneApiFiles(string gameId, GameRecord rec)
    {
        var dir = Store.GameDir(gameId);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rec.Plan?.FilePath is { } current) keep.Add(Path.GetFileName(current));
        foreach (var (api, c) in rec.ApiCompiles ?? [])
        {
            if (c[nameof(GameRecord.Plan)]?[nameof(Plan.FilePath)]?.GetValue<string>() is { } plan) keep.Add(Path.GetFileName(plan));
            keep.Add($"compiled.{api}.keys");
        }
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "plan.*.bin").Concat(Directory.EnumerateFiles(dir, "compiled.*.keys")).ToList())
                if (!keep.Contains(Path.GetFileName(f))) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{gameId}: other APIs' plan files not pruned: {e.Message}"); }
    }

    /// <summary>The key files the record's other APIs' compiles name (<see cref="AppStore.PruneKeyFiles"/> keeps them).</summary>
    internal static IEnumerable<string?> ApiKeyFiles(GameRecord? rec) => rec?.ApiCompiles?.Values
        .SelectMany(c => new[] { c[nameof(GameRecord.PlanKeysFile)]?.GetValue<string>(), c[nameof(GameRecord.WarmKeysFile)]?.GetValue<string>() }) ?? [];

    /// <summary>The other APIs' complete compiles on record, but those a Clear cache took since, for the game page and the CLI.</summary>
    IReadOnlyList<ApiCompile>? KeptApis(GameRecord rec, GpuSnapshot gpu) => rec.ApiCompiles?
        .Select(x => (Api: x.Key, At: x.Value[nameof(GameRecord.WarmedAt)]?.Deserialize<DateTimeOffset?>(AppStore.Json),
            Version: x.Value[nameof(GameRecord.WarmedDriverVersion)]?.GetValue<string>(), Id: x.Value[nameof(GameRecord.WarmedDriverId)]?.GetValue<string>()))
        .Where(x => x.At != null && !(rec.CacheClearedAt > x.At)).OrderBy(x => x.Api, StringComparer.Ordinal)
        .Select(x => new ApiCompile(x.Api, x.At!.Value, x.Version, CurrentDriver(gpu, x.Id, x.Version))).ToList() is { Count: > 0 } list ? list : null;

    /// <summary>An <see cref="ApiKey"/> as the game page names it.</summary>
    public static string KeyName(string key) => key switch { "vk" => "Vulkan", "11" => "DirectX 11", "12" => "DirectX 12", _ => "DirectX 11 and 12" };

    /// <summary>The game page's line for the compiles kept for its other graphics APIs; null = none.</summary>
    public static string? KeptApisNote(GameState s) => s.KeptApis is not { Count: > 0 } kept ? null
        : "Also compiled for " + string.Join(" and ", kept.Select(k => KeyName(k.Api) + (k.CurrentDriver ? "" : $" on an older driver ({k.DriverVersion})"))) + ".";
}
