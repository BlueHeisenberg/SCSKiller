using System.Diagnostics;
using System.Text.RegularExpressions;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Planning;

/// <summary>Source-derived root-signature rules on this machine's installed Unreal games (read-only; FF7 Rebirth never touched).
/// Manual: SCSKILLER_RS_SWEEP=1 (SCSKILLER_RS_SWEEP_GAMES=a;b limits it to names containing a or b). Per game, the
/// recording-free plan's every root signature must be accepted by the D3D12 runtime, and every planned pipeline's shaders
/// must find each resource they declare in its root signature, visible to their stage and not denied (what the runtime
/// rejects as "root signature doesn't match shader"). With SCSKILLER_GPU_TESTS=1 also ~500 of its PSOs replayed under the
/// D3D12 debug layer, under a fake exe name. Summary: %TEMP%\scskiller-tests\rs-sweep.<process id>\sweep.log.</summary>
public class UnrealRootSigSweep(ITestOutputHelper output)
{
    static readonly string Selftest = Path.Combine(Ff7.ProxyBin, "selftest.exe");

    [Trait("Needs", "Game")]
    [Fact]
    public void GeneratedRootSignaturesAreAcceptedAndMatchTheirShaders()
    {
        if (Environment.GetEnvironmentVariable("SCSKILLER_RS_SWEEP") != "1") return;
        var only = Environment.GetEnvironmentVariable("SCSKILLER_RS_SWEEP_GAMES")?.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var gpu = Environment.GetEnvironmentVariable("SCSKILLER_GPU_TESTS") == "1" && File.Exists(Selftest);
        var root = Ff7.TempDir("rs-sweep");
        var log = Path.Combine(root, "sweep.log");
        void Log(string s) { output.WriteLine(s); File.AppendAllText(log, s + Environment.NewLine); }
        Assert.True(D3D12Runtime.Available, "no D3D12 device");
        var reader = new UnrealReader(Path.Combine(root, "data"));
        var games = new IGameSource[] { new SteamSource(), new EpicSource(), new XboxSource() }
            .SelectMany(s => { try { return s.Discover(); } catch (Exception) { return []; } })
            .Where(g => !g.InstallDir.Contains("FINAL FANTASY VII REBIRTH", StringComparison.OrdinalIgnoreCase))
            .Where(g => only == null || only.Any(o => g.Name.Contains(o, StringComparison.OrdinalIgnoreCase))).ToList();
        var bad = new List<string>();
        foreach (var game in games)
        {
            EngineInfo? e;
            try { e = reader.Detect(game); } catch (Exception ex) { Log($"{game.Name}: detect failed: {ex.Message}"); continue; }
            if (e == null) continue;
            var rule = RootSig.RuleFor(e);
            var what = $"{game.Name} (Unreal {e.Version}{(e.Fork != null ? " " + e.Fork : "")}, {e.GraphicsApi}, {new Planner().Check(game, e, null, Ff7.Nvidia).Readiness})";
            if (e.Unsupported != null || e.Encrypted || rule == null) { Log($"{what}: skipped: {e.Unsupported ?? (e.Encrypted ? "encrypted" : "no rule")}"); continue; }
            var sw = Stopwatch.StartNew();
            ShaderIndex index;
            try { index = reader.Index(game, e, null, CancellationToken.None); }
            catch (Exception ex) { Log($"{what}: index failed: {ex.GetType().Name}: {ex.Message}"); continue; }
            var dir = Path.Combine(root, Regex.Replace(game.Name, @"[^\w]+", "_"));
            var plan = new Planner().Build(game, e, index, null, Ff7.Nvidia, dir, null, CancellationToken.None);
            var body = PlanFile.Read(plan.FilePath).Records.ToList();

            var sigs = body.Where(r => r.Tag == 'B').ToDictionary(r => PsoDb.Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
            var rejected = sigs.Count(s => D3D12Runtime.CreateRootSignature(s.Value) < 0);
            var parsed = sigs.ToDictionary(s => s.Key, s => Rts0(s.Value));
            var extras = string.Join(", ", new (string Name, Func<(uint Flags, List<Slot> Slots), bool> Has)[]
            {
                ("bindless", r => (r.Flags & 0xC00) != 0), ("root constants", r => r.Slots.Any(x => x.Type == 2 && x.Space == 3)),
                ("diagnostic UAV", r => r.Slots.Any(x => x.Space == 999)), ("AGS UAV", r => r.Slots.Any(x => x.Space == 0x7FFF0ADE)),
            }.Select(x => (x.Name, N: parsed.Values.Count(x.Has))).Where(x => x.N > 0).Select(x => $"{x.N} with {x.Name}"));
            var psos = body.Where(r => r.Tag == 'P').Select(r => PsoDb.ParseItem(r.Payload)).Select(i => (i.Rs, i.Stages))
                .Concat(body.Where(r => r.Tag == 'S').Select(PsoDb.Parse).Select(p => (p.Rs, p.Stages))).ToList();
            var mismatches = new Dictionary<string, int>();
            var example = new Dictionary<string, string>();
            long checkedBindings = 0;
            foreach (var (rs, stages) in psos)
                foreach (var (st, sha) in stages)
                {
                    checkedBindings += index.Shaders[sha].Bindings.Count;
                    if (Mismatch(index.Shaders[sha], (Stage)st, parsed[rs]) is not { } m) continue;
                    mismatches[m] = mismatches.GetValueOrDefault(m) + 1;
                    example.TryAdd(m, $"{sha} {index.Shaders[sha].ShaderModel} {index.Shaders[sha].Counts}");
                }
            var n = mismatches.Values.Sum();
            var amd = mismatches.Where(m => m.Key.EndsWith($"space {AgsSpace}")).Sum(m => m.Value); // AMD-only permutations, see below
            var shapes = string.Join(", ", psos.GroupBy(p => string.Join('+', p.Stages.Keys.Select(k => ((Stage)k).ToString()[..2]))).Select(g => $"{g.Key} {g.Count()}"));
            Log($"{what}: rule {rule}, platform {plan.Platform}, {index.Shaders.Count} shaders -> {psos.Count} PSOs ({shapes}); "
                + $"{sigs.Count} root signatures{(extras.Length > 0 ? $" ({extras})" : "")}, {rejected} rejected by D3D12; {n} shader/root-signature mismatches in {checkedBindings} bindings"
                + (n > 0 ? ": " + string.Join("; ", mismatches.OrderByDescending(m => m.Value).Take(5).Select(m => $"{m.Key} x{m.Value} (e.g. {example[m.Key]})")) : "")
                + $" ({sw.Elapsed.TotalSeconds:F0}s)");
            // a UE 4 shader using AMD's AGS intrinsics (u0 in the AGS space) has no slot in a stock 4.x root signature (UE added it
            // in 5.0): the game can't create that pipeline either, unless its fork adds the slot, so it isn't a rule error
            if (rejected + n - (e.Version.StartsWith('4') ? amd : 0) > 0) bad.Add(what);

            if (gpu)
            {
                var work = Sample(plan, body, game, e, reader, Path.Combine(dir, "sample"), 500);
                var (ok, failed, rsErrors, text) = DebugWarm(work, $"scsk_uers_{Random.Shared.Next(100000, 999999)}.exe");
                Log($"  debug layer: {ok} replayed ok, {failed} rejected, {rsErrors} root-signature errors{(failed + rsErrors > 0 ? "\n" + text : "")}");
                if (failed + rsErrors > 0) bad.Add(what + " (debug layer)");
            }
            GC.Collect();
        }
        Assert.Empty(bad);
    }

    /// <summary>The mismatch check itself: it flags a resource the root signature lacks and a denied stage.</summary>
    [Fact]
    public void MismatchCheckCatchesMissingResources()
    {
        static ShaderInfo S(Stage st, ResourceCounts c, params Binding[] b) => new("", st, "", 0, c, b, [], []);
        var vs = S(Stage.Vertex, new(1, 0, 0, 0), new Binding("cbv", 0, 0, 1));
        var ps = S(Stage.Pixel, new(1, 1, 0, 0), new Binding("cbv", 0, 0, 1), new Binding("srv", 0, 3, 1), new Binding("sampler", 1000, 2, 1));
        var rs = Rts0(RootSig.Serialize(RootSig.Build(Rules.Ue51, new Dictionary<Stage, ShaderInfo> { [Stage.Vertex] = vs, [Stage.Pixel] = ps }, false), RootSig.Ue426Samplers));
        Assert.Null(Mismatch(vs, Stage.Vertex, rs));
        Assert.Null(Mismatch(ps, Stage.Pixel, rs));                                                   // t3 in the 64-SRV table, s2 space 1000 static
        Assert.Equal("Pixel uav space 0", Mismatch(ps with { Bindings = [.. ps.Bindings, new Binding("uav", 0, 0, 1)] }, Stage.Pixel, rs));
        Assert.Equal("Vertex srv space 0", Mismatch(vs with { Bindings = [new Binding("srv", 0, 0, 1)] }, Stage.Vertex, rs)); // no vertex SRV table
        Assert.Equal("Geometry denied", Mismatch(vs with { Stage = Stage.Geometry }, Stage.Geometry, rs));
        Assert.Equal("Pixel srv space 0", Mismatch(ps with { Bindings = [new Binding("srv", 0, 60, 8)] }, Stage.Pixel, rs));   // t60-t67 past t63
    }

    const uint AgsSpace = 0x7FFF0ADE; // AGS_DX12_SHADER_INSTRINSICS_SPACE_ID

    // D3D12_SHADER_VISIBILITY and D3D12_ROOT_SIGNATURE_FLAG_DENY_* of each stage
    static uint Vis(Stage s) => s switch { Stage.Vertex => 1, Stage.Hull => 2, Stage.Domain => 3, Stage.Geometry => 4, Stage.Pixel => 5, Stage.Amplification => 6, Stage.Mesh => 7, _ => 0 };
    static uint Deny(Stage s) => s switch { Stage.Vertex => 0x2, Stage.Hull => 0x4, Stage.Domain => 0x8, Stage.Geometry => 0x10, Stage.Pixel => 0x20, Stage.Amplification => 0x100, Stage.Mesh => 0x200, _ => 0 };

    /// <summary>One register range a root signature provides: descriptor range type (SRV 0, UAV 1, CBV 2, sampler 3).</summary>
    internal sealed record Slot(uint Vis, uint Type, uint Base, uint Count, uint Space);

    /// <summary>The RTS0 part of a serialized 1.1 root signature, read independently of RootSig: flags and every register range
    /// (table ranges, root descriptors, root constants as a CBV, static samplers).</summary>
    internal static (uint Flags, List<Slot> Slots) Rts0(byte[] blob)
    {
        var off = Enumerable.Range(0, BitConverter.ToInt32(blob, 28)).Select(i => BitConverter.ToInt32(blob, 32 + 4 * i)).First(o => blob.AsSpan(o, 4).SequenceEqual("RTS0"u8)) + 8;
        uint U(long o) => BitConverter.ToUInt32(blob, off + (int)o);
        Assert.Equal(2u, U(0)); // D3D_ROOT_SIGNATURE_VERSION_1_1
        var slots = new List<Slot>();
        for (var i = 0u; i < U(4); i++)
        {
            var (type, vis, p) = (U(U(8) + 12 * i), U(U(8) + 12 * i + 4), U(U(8) + 12 * i + 8));
            if (type == 0)
                for (var r = 0u; r < U(p); r++) { var q = U(p + 4) + 24 * r; slots.Add(new(vis, U(q), U(q + 8), U(q + 4), U(q + 12))); }
            else slots.Add(new(vis, type switch { 3 => 0u, 4 => 1u, _ => 2u }, U(p), 1, U(p + 4))); // 1 constants, 2 CBV, 3 SRV, 4 UAV
        }
        for (var s = 0u; s < U(12); s++) { var q = U(16) + 52 * s; slots.Add(new(U(q + 48), 3, U(q + 40), 1, U(q + 44))); }
        return (U(20), slots);
    }

    /// <summary>The first resource the shader declares that its stage can't reach through the root signature, as a
    /// "stage class space" key; null = all bound.</summary>
    internal static string? Mismatch(ShaderInfo sh, Stage stage, (uint Flags, List<Slot> Slots) rs)
    {
        if (sh.Bindings.Count > 0 && (rs.Flags & Deny(stage)) != 0) return $"{stage} denied";
        foreach (var b in sh.Bindings)
        {
            var type = b.Class switch { "srv" => 0u, "uav" => 1u, "cbv" => 2u, "sampler" => 3u, _ => 9u };
            if (type == 9) continue;
            if (!rs.Slots.Any(s => (s.Vis == 0 || s.Vis == Vis(stage)) && s.Type == type && s.Space == (uint)b.Space && s.Base <= (uint)b.Lower
                    && (s.Count == uint.MaxValue || (b.Count >= 0 && (ulong)b.Lower + (ulong)b.Count <= (ulong)s.Base + s.Count))))
                return $"{stage} {b.Class} space {b.Space}";
        }
        return null;
    }

    /// <summary>Every n-th item, the templates they use (all synthesized for a recording-free plan) and their root signatures,
    /// materialized.</summary>
    string Sample(Plan full, List<PsoDb.Rec> body, Game game, EngineInfo engine, IEngineReader reader, string dir, int count)
    {
        var all = body.Where(r => r.Tag == 'P').ToList();
        var items = all.Where((_, i) => i % Math.Max(1, all.Count / count) == 0).Take(count).ToList();
        var used = items.Select(i => PsoDb.ParseItem(i.Payload).Template).ToHashSet();
        var templates = body.Where(r => r.Tag is 'S' or 'G' or 'C' && used.Contains(r.Key)).ToList();
        var rs = items.Select(i => PsoDb.ParseItem(i.Payload).Rs).Concat(templates.Select(t => PsoDb.Parse(t).Rs)).ToHashSet();
        var sigs = body.Where(r => r.Tag == 'B' && rs.Contains(PsoDb.Hex(r.Payload.AsSpan(0, 20))));
        var plan = full with { FilePath = Path.Combine(dir, "sample.bin") };
        PlanFile.Write(plan, sigs.Concat(templates).Concat(items));
        var work = Path.Combine(dir, "work");
        new Planner().Materialize(plan, game, engine, reader, null, work, CancellationToken.None);
        return work;
    }

    /// <summary>selftest.exe renamed next to the proxy in <paramref name="work"/>, debug layer on; the root-signature errors among
    /// the runtime's messages.</summary>
    static (long Ok, long Failed, int RootSigErrors, string Log) DebugWarm(string work, string exe)
    {
        using var gpu = GpuLock();
        File.Copy(Selftest, Path.Combine(work, exe), true);
        File.Copy(Path.Combine(Path.GetDirectoryName(Selftest)!, "d3d12.dll"), Path.Combine(work, "d3d12.dll"), true);
        var psi = new ProcessStartInfo(Path.Combine(work, exe), ["debugwarm", "1"]) { RedirectStandardOutput = true, UseShellExecute = false };
        psi.Environment["SCSKILLER_THREADS"] = "4";
        psi.Environment["SCSKILLER_SELFTEST_UNARMED"] = "1";   // a warm host: the proxy admits it unarmed
        string o;
        using (var p = Process.Start(psi)!) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
        var m = Regex.Match(o, @"replayed ok=(\d+) fail=(\d+)");
        Assert.True(m.Success, o);
        var rsErrors = Regex.Matches(o, @"^\[\d+ x(\d+)\] .*root signature.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Sum(x => int.Parse(x.Groups[1].Value));
        return (long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value), rsErrors, o);
    }

    static IDisposable GpuLock()
    {
        var path = TestEnv.GpuLockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var deadline = DateTime.UtcNow.AddMinutes(30); ; Thread.Sleep(5000))
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(30)) File.Delete(path);
            try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { }
        }
    }
}
