using SCSKiller.Core.CryEngine;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.App;

public sealed partial class ScsKiller
{
    /// <summary>SCSKILLER_TEST_STATICS_TRACE: a file a lab's compile appends each pass-1 record's stage id and outcome to. Never a setting.</summary>
    internal string? StaticsTrace { get; set; } = Environment.GetEnvironmentVariable("SCSKILLER_TEST_STATICS_TRACE") is { Length: > 0 } v ? v : null;

    /// <summary>The AMD sampler tables by build stamp; null: <see cref="CryAmdSamplers.Tables"/>. Tests only.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyDictionary<CryAmdLearn.Place, CrySampler>>? CryAmdTables { get; set; }

    /// <summary>What Materialize builds from: on NVIDIA, a CRYENGINE layouts plan with its static-sampler pipelines at the
    /// slots the game's own D3DSCache records give (<see cref="CryStaticsPass"/>), with the root signatures each warm process
    /// holds first (<see cref="CryStatics.HoldFile"/>); else the plan as it is. The game's cache is only read, and never while
    /// the game runs. AmdLeftOut: the pipelines the AMD plan left out (<see cref="AmdStaticsPlan"/>).</summary>
    (Plan Plan, byte[]? Hold, long AmdLeftOut) StaticsPlan(Game game, GameRecord rec, EngineInfo engine, Plan plan, int threads, CancellationToken ct)
    {
        if (AmdStatics(engine, plan))
        {
            var (amd, leftOut) = AmdStaticsPlan(game, plan);
            return (amd, null, leftOut);
        }
        if (!OperatingSystem.IsWindows() || Vendor.Vendor != GpuVendor.Nvidia || engine.Family != CryEngineReader.Family || !engine.Version.Contains(CryEngineReader.Layouts)
            || plan.Platform == Vulkan.VulkanPlan.Platform)
            return (plan, null, 0);
        var layouts = CryLayouts.Read(game.InstallDir);
        if (!CryEngineReader.Builds(layouts)) return (plan, null, 0);
        Dictionary<string, List<CryStatics.Str>>? records = null;
        string? noCache = null;
        if (GameRunning(game)) noCache = "the game is running";
        else
        {
            var read = GameD3DSCacheAll(game, rec).Where(f => f.Name.EndsWith(".dxcache", StringComparison.OrdinalIgnoreCase)).Select(f => CryStatics.ReadDb(f.FullName)).OfType<List<CryStatics.Str>>().ToList();
            if (read.Count == 0) noCache = "no D3DSCache of the game";
            else records = CryStatics.Index(read.SelectMany(x => x));
        }
        // its own: the compile's warmer carries the game's layer, AGS and Aftermath
        var warmer = _warmer is Warmer ? new Warmer(Vendor) { Log = Log } : _warmer;
        var pass = new CryStaticsPass
        {
            D3DSCacheRoot = Path.Combine(LocalAppData, "D3DSCache"),
            NvCache = Path.Combine(LocalAppData, "NVIDIA", "DXCache"),
            Dir = Path.Combine(Store.GameDir(game.Id), "statics"),
            Materialize = (file, work) =>
            {
                var planner = _planner as Planner;
                var inMemory = planner?.GenInMemory ?? false;
                if (planner != null) planner.GenInMemory = false;
                try { _planner.Materialize(plan with { FilePath = file }, game, engine, _reader, null, work, ct); }
                finally { if (planner != null) planner.GenInMemory = inMemory; }
            },
            Warm = (exe, work) => warmer.Start(game with { ExePath = Path.Combine(Path.GetDirectoryName(game.ExePath)!, exe) }, work, new WarmOptions(1, Settings.Priority), null).Completion,
            Lanes = Math.Max(1, threads),
            Log = Log,
            Trace = StaticsTrace is { } trace ? (stage, why) => { lock (trace) File.AppendAllText(trace, $"{stage} {why}\n"); } : null,
        };
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var made = pass.Run(plan, body, layouts, records, ct);
        var c = pass.Counts;
        Log?.Report($"{game.Name}: static samplers: {c.GetValueOrDefault("pipelines")} pipelines, {c.GetValueOrDefault("mapped")} at the game's slots"
            + (noCache != null ? $", none read ({noCache})" : records != null ? $" from {records.Count} stages of its cache" : "")
            + string.Concat(c.Where(x => x.Key.StartsWith("fallback.", StringComparison.Ordinal) || x.Key.StartsWith("rows.", StringComparison.Ordinal) || x.Key == "defaultSlots").OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $", {x.Key} {x.Value}"))
            + (pass.Pass1Time > TimeSpan.Zero ? FormattableString.Invariant($"; pass 1 {pass.Pass1Time.TotalSeconds:F1}s on {pass.Lanes} processes, {pass.ThrowawayBytes / 1024.0 / 1024:F1} MB of throwaway caches deleted, {c.GetValueOrDefault("leftover")} left") : ""));
        if (made == null) return (plan, null, 0);
        var file = Path.Combine(Store.GameDir(game.Id), "statics-plan.bin");
        var mapped = plan with { FilePath = file };
        PlanFile.Write(mapped, made.Value.Body);
        return (mapped, CryStatics.HoldBytes(made.Value.Holds), 0);
    }

    bool AmdStatics(EngineInfo engine, Plan plan) => OperatingSystem.IsWindows() && Vendor.Vendor == GpuVendor.Amd && plan.Platform != Vulkan.VulkanPlan.Platform
        && engine.Family == CryEngineReader.Family && engine.Version.Contains(CryAmdSamplers.Suffix);

    /// <summary>What <see cref="StaticsPlan"/> leaves out of <paramref name="plan"/> on AMD, without writing it: for a compile that
    /// goes on from kept work, which doesn't materialize again.</summary>
    long AmdLeftOut(Game game, EngineInfo engine, Plan plan) => AmdStatics(engine, plan) ? AmdStaticsPlan(game, plan, write: false).LeftOut : 0;

    /// <summary>On AMD, a CRYENGINE layouts plan with its pipelines' static samplers at the build's learned states, a place the
    /// table lacks at its best guess (<see cref="CryAmdSamplers"/>); the plan as it is without a table. LeftOut: the pipelines
    /// left out (a place with no state, a root signature only layouts no record names build).</summary>
    (Plan Plan, long LeftOut) AmdStaticsPlan(Game game, Plan plan, bool write = true)
    {
        var layouts = CryLayouts.Read(game.InstallDir);
        if (CryAmdSamplers.For(game, layouts, CryAmdTables) is not { } table)
        {
            Log?.Report($"{game.Name}: static samplers: no table for this build, the plan as it is");
            return (plan, 0);
        }
        var (body, counts) = CryAmdSamplers.Apply(PlanFile.Read(plan.FilePath).Records.ToList(), layouts, table,
            CryLayouts.PipelineLayouts(game.InstallDir).Values.SelectMany(x => x).Select(x => x.Id).ToHashSet());
        var leftOut = counts.GetValueOrDefault("unlearned") + counts.GetValueOrDefault("unnamed");
        Log?.Report($"{game.Name}: static samplers from the build's table: {counts.GetValueOrDefault("pipelines")} pipelines planned, {counts.GetValueOrDefault("learned")} on learned or sampler-free layouts, "
            + $"{counts.GetValueOrDefault("guessed")} on a best guess ({counts.GetValueOrDefault("guessedByDefault")} at the engine default), {leftOut} left out; {string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}"))}");
        if (body == null || !write) return (plan, leftOut);
        var mapped = plan with { FilePath = Path.Combine(Store.GameDir(game.Id), "statics-plan.bin") };
        PlanFile.Write(mapped, body);
        return (mapped, leftOut);
    }
}
