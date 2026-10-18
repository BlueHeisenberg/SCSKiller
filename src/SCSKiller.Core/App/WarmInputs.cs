using System.Security.Cryptography;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.App;

/// <summary>The records a warm takes as input, each with one bit: whether every blob it names directly is in the sources a
/// warm reads (the recordings, the plan, the packs, the build's shaders, the DLLs next to the exe). Nothing here predicts
/// what compiles: the count of new pipelines compares these with the ones taken when the last complete warm started.</summary>
public static class WarmInputs
{
    /// <summary>The recordings' part, read once as their union (the first's records first), keeping no blob bytes: each
    /// record's key with the blobs it names that neither the recordings nor <paramref name="elsewhere"/> (the install or a
    /// DLL) hold, and the recordings' blobs. With <paramref name="nvidia"/> a state object counts once per
    /// <see cref="StateObjectIdentity">identity</see> and NVAPI state, keyed by the SHA-1 of the two (whichever record of it
    /// comes first), lacking a blob only when each of its records does: the same addition on another base, or under a launch's export
    /// names, compiles nothing new there, and the same one under another NVAPI state does. NVIDIA compiles and caches each
    /// stage of a pipeline on its own, keyed on the shader, the whole root signature and the NVAPI state (NvidiaBackend.Caps
    /// PerStageCache and StateIndependentCache, <see cref="UnitPolicy.Nvidia"/>), so there a recorded pipeline counts as its
    /// stages, each keyed by the SHA-1 of the four, with every blob the record names (the warm drops a pipeline lacking any)
    /// and as with an identity, lacking one only when each record with the stage does: other fixed-function state or another pairing of
    /// stages compiled before compiles nothing new. One with a stream output declaration, which the measured key doesn't
    /// cover, counts by its key. <see cref="Members"/> holds the keys of an identity's or a stage's records. AMD caches a whole state object and hits only an exact repeat of it and its AddToStateObject
    /// chain (AmdBackend), so elsewhere each record counts by its key, except one no later launch creates again: one with a
    /// launch's alias (<see cref="HasLaunchAlias"/>), or one building on such a record (its base or a linked collection;
    /// recorded after it). The warm replays every record either way.</summary>
    public sealed record Recorded(List<(string Key, List<List<string>> Missing)> Records, HashSet<string> Blobs)
    {
        /// <summary>An identity's or a stage's key -> the keys of its records, in the recordings' order.</summary>
        public Dictionary<string, List<string>> Members { get; init; } = [];

        /// <summary>The state object records no later launch creates again, which aren't inputs (AMD only).</summary>
        public int LaunchOnly { get; init; }

        /// <summary>Its pipelines count as their stages (NVIDIA).</summary>
        public bool PerStage { get; init; }

        /// <summary>False: no ray tracing state object is an input, read or planned (<see cref="Planner.LeftOutWithoutRt"/>).</summary>
        public bool RayTracing { get; init; } = true;

        /// <param name="leaveOut">records that are no input: another texture filtering setting's (<see cref="Planning.SamplerVariants.Foreign"/>)</param>
        /// <param name="rayTracing">false: ray tracing state objects are no input (<see cref="Planner.LeftOutWithoutRt"/>)</param>
        public static Recorded Read(IReadOnlyList<string> recordings, Func<string, bool> elsewhere, bool nvidia = false, IReadOnlySet<string>? leaveOut = null,
            bool rayTracing = true)
        {
            var blobs = new HashSet<string>();
            // NVIDIA keys on the NVAPI state a record is created with (selftest nvext). The replay's: the last 'N' per record
            // in the recordings' union, which drops an 'N' an earlier recording has (Community.Union, proxy.cpp g_nvext), then
            // the union's option fixes, as Planner.Materialize writes them
            var nv = new Dictionary<string, NvState>();
            var union = new HashSet<string>();
            if (nvidia)
            {
                var recs = recordings.Where(File.Exists).SelectMany(PsoDb.Read).Where(r => r.Tag != 'B' && (r.Tag != 'N' || union.Add(r.Key))).ToList();
                foreach (var r in recs.Concat(RtCollections.OptionFixes(recs)))
                    if (r.Tag == 'N' && r.Payload.Length == NvState.Size) { var n = NvState.Parse(r); nv[n.Target] = n; }
            }
            string State(Rec r) => StateOf(nv, r);
            string Identity(Rec r) => Hex(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes($"so{StateObjectIdentity(r)}|{State(r)}")));
            var seen = leaveOut is null ? new HashSet<string>() : new HashSet<string>(leaveOut);
            var launch = new HashSet<string>();
            var lacking = new Dictionary<string, List<string>>();   // a state object's key -> the blobs it or what it builds on lacks
            var members = new Dictionary<string, List<string>>();
            var records = new List<(string Key, List<List<string>> Missing)>();   // each of its records' missing blobs
            var at = new Dictionary<string, int>();
            void Add(string key, List<string> gone)
            {
                if (at.TryGetValue(key, out var i)) records[i].Missing.Add(gone);
                else { at[key] = records.Count; records.Add((key, [gone])); }
            }
            foreach (var db in recordings.Where(File.Exists))
                foreach (var r in PsoDb.Read(db))
                    if (r.Tag == 'B') { if (r.Payload.Length >= 20) blobs.Add(Hex(r.Payload.AsSpan(0, 20))); }
                    else if (r.Tag is not ('N' or 'L' or 'W') && (rayTracing || !Planner.LeftOutWithoutRt(r)) && seen.Add(r.Key))
                    {
                        if (!nvidia && IsStateObject(r.Tag) && (HasLaunchAlias(r) || Depends(r).Any(launch.Contains)))
                        {
                            launch.Add(r.Key);
                            continue;
                        }
                        if (nvidia && Stages(r) is { Count: > 0 } stages)
                        {
                            var missing = Missing(r, h => blobs.Contains(h) || elsewhere(h));
                            foreach (var (stage, shader, rs) in stages)
                            {
                                var unit = Unit(stage, shader, rs, State(r));
                                if (!members.TryAdd(unit, [r.Key])) members[unit].Add(r.Key);
                                Add(unit, missing);
                            }
                            continue;
                        }
                        var gone = Missing(r, h => blobs.Contains(h) || elsewhere(h));
                        if (IsStateObject(r.Tag))
                        {
                            // the warm drops a state object whose collection or base it drops (Planner.Materialize)
                            gone = [.. gone.Union(Depends(r).SelectMany(d => lacking.GetValueOrDefault(d) ?? []))];
                            if (gone.Count > 0) lacking[r.Key] = gone;
                        }
                        var key = nvidia && IsStateObject(r.Tag) ? Identity(r) : r.Key;
                        if (key != r.Key && !members.TryAdd(key, [r.Key])) members[key].Add(r.Key);
                        Add(key, gone);
                    }
            // a blob may come later; one record with none missing is enough, else the plan may still bring one record's (Of)
            List<List<string>> Left(List<List<string>> each) =>
                each.Select(m => m.Count == 0 ? m : m.Where(h => !blobs.Contains(h)).ToList()).ToList() is var left && left.Any(m => m.Count == 0) ? [[]] : left;
            return new([.. records.Select(r => (r.Key, Left(r.Missing)))], blobs) { Members = members, LaunchOnly = launch.Count, PerStage = nvidia, RayTracing = rayTracing };
        }
    }

    static string StateOf(Dictionary<string, NvState> nv, Rec r) => nv.TryGetValue(r.Key, out var n) && !n.Bare ? $"{n.Slot},{n.Space},{n.Options}" : "none";

    static string Unit(int stage, string shader, string rs, string state) => Hex(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes($"stage{stage}|{shader}|{rs}|{state}")));

    /// <summary>A pipeline record's stages with its root signature; none for a stream output declaration or a record that
    /// doesn't parse.</summary>
    static List<(int Stage, string Shader, string Rs)> Stages(Rec r)
    {
        if (r.Tag is not ('G' or 'C' or 'S')) return [];
        try { return StreamOutputOf(r) != null ? [] : Parse(r) is var p ? [.. p.Stages.Select(s => (s.Key, s.Value, p.Rs))] : []; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException or OverflowException) { return []; }
    }

    static List<string> Depends(Rec r)
    {
        try { return ParseStateObject(r).Depends; }
        catch (InvalidDataException) { return []; }
    }

    static List<string> Missing(Rec r, Func<string, bool> has)
    {
        try { return [.. Rehydrate.References([r]).Where(h => !has(h))]; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException) { return []; }
    }

    /// <summary>Each input's key, with a trailing '!' when a blob it names isn't at hand, then for a state object identity
    /// or a stage the keys of its records ('|' before each): the recordings' records, then the plan's (a pack entry unwrapped), then
    /// <paramref name="packEntries"/>. On NVIDIA a recorded stage then has the keys of the plan's and the packs' pipelines that
    /// have it ('~' before each, <see cref="CompiledBy"/>).</summary>
    public static HashSet<string> Of(Recorded recorded, string? planFile, IEnumerable<Rec> packEntries, Func<string, bool> elsewhere)
    {
        var blobs = new HashSet<string>(recorded.Blobs);
        var body = Planner.PlanBody(planFile);
        foreach (var r in body) if (r.Tag == 'B' && r.Payload.Length >= 20) blobs.Add(Hex(r.Payload.AsSpan(0, 20)));
        bool Has(string h) => blobs.Contains(h) || elsewhere(h);
        var planned = Planned(body, packEntries, recorded.RayTracing).ToList();
        // a recorded stage a planned pipeline has, under the same NVAPI state, is compiled by it whatever its other state
        var compiledBy = new Dictionary<string, List<string>>();
        if (recorded.PerStage && recorded.Members.Count > 0)
        {
            var nv = new Dictionary<string, NvState>();
            foreach (var r in body) if (r.Tag == 'N' && r.Payload.Length == NvState.Size) { var n = NvState.Parse(r); nv[n.Target] = n; }
            foreach (var (key, r) in planned)
                if (Stages(r) is { Count: > 0 } stages && StateOf(nv, r) is var state)
                    foreach (var (stage, shader, rs) in stages)
                        if (Unit(stage, shader, rs, state) is var unit && recorded.Members.ContainsKey(unit))
                            (compiledBy.TryGetValue(unit, out var by) ? by : compiledBy[unit] = []).Add(key);
        }
        var inputs = new HashSet<string>();
        var seen = new HashSet<string>();
        foreach (var (key, missing) in recorded.Records)
        {
            seen.Add(key);
            var m = recorded.Members.GetValueOrDefault(key);
            if (m != null) seen.UnionWith(m);   // a plan's copy of a recorded pipeline is that pipeline
            inputs.Add((missing.Any(x => x.All(Has)) ? key : key + "!") + (m != null ? "|" + string.Join('|', m) : "")
                + (compiledBy.TryGetValue(key, out var by) ? "~" + string.Join('~', by) : ""));
        }
        foreach (var (key, r) in planned)
            if (seen.Add(key)) inputs.Add(Missing(r, Has).Count == 0 ? key : key + "!");
        return inputs;
    }

    /// <summary>The plan's pipelines and state objects (a pack entry unwrapped), then <paramref name="packEntries"/>, by input key.
    /// Without <paramref name="rayTracing"/> the plan's ray tracing is left out.</summary>
    static IEnumerable<(string Key, Rec Rec)> Planned(List<Rec> body, IEnumerable<Rec> packEntries, bool rayTracing = true) =>
        Planner.PlanInputs(body).Where(x => rayTracing || !Planner.LeftOutWithoutRt(x.Rec)).Select(x => x.Rec.Tag == 'M' ? (MiddlewarePacks.Unwrap(x.Rec).Entry.Key, MiddlewarePacks.Unwrap(x.Rec).Entry) : x)
            .Concat(packEntries.Select(r => (r.Key, r)));

    /// <summary>NVIDIA: each planned pipeline's stages, keyed as a recorded stage is (shader, root signature, NVAPI state).</summary>
    static IEnumerable<(string Key, List<string> Units)> PlannedUnits(string? planFile, IEnumerable<Rec> packEntries)
    {
        var body = Planner.PlanBody(planFile);
        var nv = new Dictionary<string, NvState>();
        foreach (var r in body) if (r.Tag == 'N' && r.Payload.Length == NvState.Size) { var n = NvState.Parse(r); nv[n.Target] = n; }
        foreach (var (key, r) in Planned(body, packEntries))
            if (Stages(r) is { Count: > 0 } stages && StateOf(nv, r) is var state)
                yield return (key, stages.Select(s => Unit(s.Stage, s.Shader, s.Rs, state)).ToList());
    }

    /// <summary>NVIDIA: the stages a warm of <paramref name="inputs"/> compiled with the plan's pipelines, for its key file: only
    /// those it took whole (an input lacking a blob is dropped) and that don't crash the driver (<paramref name="crashed"/>).</summary>
    public static IEnumerable<string> PlannedStages(string? planFile, IReadOnlySet<string> inputs, Func<string, bool> crashed) =>
        PlannedUnits(planFile, []).Where(p => inputs.Contains(p.Key) && !crashed(p.Key)).SelectMany(p => p.Units).Distinct();

    /// <summary>NVIDIA: of <paramref name="keys"/>, the plan's and the packs' pipelines whose every stage
    /// <paramref name="baseline"/> holds: the same stages at other fixed-function state compile nothing new.</summary>
    public static HashSet<string> StagesTaken(string? planFile, IEnumerable<Rec> packEntries, IReadOnlySet<string> keys, IReadOnlySet<string> baseline) =>
        [.. PlannedUnits(planFile, packEntries).Where(p => keys.Contains(p.Key) && p.Units.All(baseline.Contains)).Select(p => p.Key)];

    public static HashSet<string> Of(IReadOnlyList<string> recordings, string? planFile, IEnumerable<Rec> packEntries, Func<string, bool> elsewhere) =>
        Of(Recorded.Read(recordings, elsewhere), planFile, packEntries, elsewhere);

    public static string Key(string input) => input[..40];

    /// <summary>A blob the input names isn't at hand: the warm skips it (a shader not in this install).</summary>
    public static bool Lacks(string input) => input.Length > 40 && input[40] == '!';

    /// <summary>The keys of the records an input stands for: a state object identity's or a stage's records, else its own key.</summary>
    public static IEnumerable<string> Records(string input) => input.Contains('|') ? input.Split('~')[0].Split('|').Skip(1) : [Key(input)];

    /// <summary>The planned pipelines that have a recorded stage (NVIDIA): a warm that compiled one compiled the stage.</summary>
    public static IEnumerable<string> CompiledBy(string input) => input.Split('~').Skip(1);

    /// <summary>A baseline (key file of <see cref="Token"/>s) took this input: it holds its key, or it lacks a blob now and
    /// the baseline has it so too. Only gaining a blob makes a taken input new again; losing one doesn't. A baseline written
    /// before state object identities holds their records' keys instead: one of them takes the identity, whatever NVAPI
    /// state it was warmed under, until the next warm writes the identity's key; the same for a stage. A key file written
    /// since holds no recorded state object's or pipeline's record key on NVIDIA but a plan's copy of one, which is that
    /// pipeline. A planned pipeline with the stage takes it unless it <paramref name="crashed"/>.</summary>
    public static bool Taken(IReadOnlySet<string> baseline, string input, Func<string, bool>? crashed = null)
    {
        var missing = Lacks(input);
        bool Holds(string key) => baseline.Contains(key) || missing && baseline.Contains(Token(key + "!"));
        return Holds(Key(input)) || input.Contains('|') && Records(input).Any(Holds)
            || CompiledBy(input).Any(k => baseline.Contains(k) && crashed?.Invoke(k) != true);
    }

    /// <summary>The new pipelines among <paramref name="inputs"/>, each once by its first record: none lacking a blob, none
    /// <paramref name="baseline"/> took (null: none is taken), none with a record that <paramref name="crashed"/>. A crashed
    /// record's stage or identity is left out whole: the input doesn't say whether another record of it is complete or
    /// builds on a crashed base.</summary>
    public static List<string> Fresh(IEnumerable<string> inputs, IReadOnlySet<string>? baseline, Func<string, bool> crashed) =>
        [.. inputs.Where(i => !Lacks(i) && !crashed(Key(i)) && !Records(i).Any(crashed) && (baseline == null || !Taken(baseline, i, crashed)))
            .Select(i => Records(i).First()).Distinct()];   // a pipeline with two new stages is one

    /// <summary>An input as a key file holds it: its key, or for one with a blob missing the SHA-1 of its key and '!', so
    /// that the same record with its blobs at hand is another input.</summary>
    public static string Token(string input) =>
        input.Length == 40 || input[40] != '!' ? input[..40] : Convert.ToHexStringLower(SHA1.HashData([.. Convert.FromHexString(input[..40]), (byte)'!']));
}
