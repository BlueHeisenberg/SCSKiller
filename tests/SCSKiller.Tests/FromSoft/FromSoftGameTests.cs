using System.Diagnostics;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Carved;
using SCSKiller.Core.FromSoft;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.FromSoft;

/// <summary>The FromSoftware reader on this machine's Steam installs (read-only; skipped when absent): detect, check, index,
/// plan offline (NVIDIA caps, a temp folder), and a sample of shaders read back by hash. Anti-cheat games (Elden Ring,
/// Nightreign) are only read as files: never launched, nothing written next to them. The archive keys come from each exe
/// at run time, into a fresh data folder every run; Dark Souls III's exe doesn't carry them in the clear, so they're
/// downloaded (UXM's, pinned by commit and SHA-256; offline, Detect refuses without naming the key file).</summary>
[Trait("Needs", "Game")]
public class FromSoftGameTests(ITestOutputHelper output)
{
    static Game G(string id, string name, string dir, string exe) => new(id, name, Store.Steam, dir, Path.Combine(dir, exe));

    /// <summary>KeysInExe: the exe carries its archives' keys in the clear (DSR has no archives).</summary>
    public static readonly Dictionary<string, (Game Game, string Version, string Api, bool KeysInExe)> Games = new()
    {
        ["DS3"] = (G("steam:374320", "DARK SOULS III", TestEnv.GameDir(@"DARK SOULS III"), @"Game\DarkSoulsIII.exe"), "DXBC", "D3D11", false),
        ["DSR"] = (G("steam:570940", "DARK SOULS: REMASTERED", TestEnv.GameDir(@"DARK SOULS REMASTERED"), "DarkSoulsRemastered.exe"), "DXBC", "D3D11", true),
        ["ER"] = (G("steam:1245620", "ELDEN RING", TestEnv.GameDir(@"ELDEN RING"), @"Game\eldenring.exe"), "DXIL+RTS0", "D3D12", true),
        ["NR"] = (G("steam:2622380", "ELDEN RING NIGHTREIGN", TestEnv.GameDir(@"ELDEN RING NIGHTREIGN"), @"Game\nightreign.exe"), "DXIL+RTS0", "D3D12", true),
    };

    [Theory]
    [InlineData("DS3")]
    [InlineData("DSR")]
    [InlineData("ER")]
    [InlineData("NR")]
    public void DetectsIndexesAndPlansInstalledGame(string key)
    {
        var (game, version, api, keysInExe) = Games[key];
        if (!File.Exists(game.ExePath)) return;
        // the app discovers the game with this exe (the reader keys on its name, the warm stages under it), not start_protected_game.exe
        Assert.Equal(game.ExePath, new SteamSource().Discover().Single(g => g.Id == game.Id).ExePath, ignoreCase: true);
        var data = Ff7.TempDir($"fromsoft-data-{key}");
        var reader = new FromSoftReader(data);
        var sw = Stopwatch.StartNew();
        var engine = reader.Detect(game)!;
        var check = new Planner().Check(game, engine, null, Ff7.Nvidia);
        output.WriteLine($"{game.Name}: detect {sw.Elapsed.TotalSeconds:F1}s: {engine}; anti-cheat {GameFiles.DetectAntiCheat(game)}; check: {check}");
        var keyFile = new SoulsKeys(data).KeyFile(game);
        if (!keysInExe && engine.Unsupported != null)
        {
            Assert.DoesNotContain(keyFile, engine.Unsupported); // offline: plain words, where a user can put them goes to the log
            output.WriteLine($"  not indexed: the archive keys couldn't be downloaded ({engine.Unsupported})");
            return;
        }
        Assert.Equal((FromSoftReader.Family, version, api, (string?)null), (engine.Family, engine.Version, engine.GraphicsApi, engine.Unsupported));
        Assert.Equal(Readiness.Ready, check.Readiness);
        // the app's chain picks this reader (the carver would call these games packed)
        Assert.Equal(engine, new EngineReaders(("Unreal", new Stub()), (FromSoftReader.Family, reader), (CarvedReader.Family, new CarvedReader())).Detect(game));

        sw.Restart();
        var index = reader.Index(game, engine, new Progress<string>(output.WriteLine), CancellationToken.None);
        var indexTime = sw.Elapsed;
        var shaders = index.Shaders.Values.ToList();
        var graphics = shaders.Where(s => s.Stage is not (Stage.Compute or Stage.Library)).ToList();
        output.WriteLine($"  index {indexTime.TotalSeconds:F1}s: {shaders.Count} shaders ({string.Join(", ", shaders.GroupBy(s => s.Stage).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"))}), "
            + $"with RTS0: {shaders.Count(s => s.RootSignature != null)} (graphics {graphics.Count(s => s.RootSignature != null)}/{graphics.Count}), "
            + $"{index.Maps.Count} maps, platforms {string.Join(", ", index.Platforms)}");
        Assert.True(shaders.Count > 1000);
        if (key == "DS3") Assert.Equal(4109, shaders.Count); // the downloaded keys open every archive
        if (FromSoftReader.TitleOf(game)!.Archives.Length > 0) Assert.StartsWith("exe ", File.ReadLines(keyFile).First()); // found in the exe (or downloaded), kept locally
        if (version.EndsWith("+RTS0")) Assert.True(graphics.Count(s => s.RootSignature != null) >= 0.9 * graphics.Count);

        var dir = Ff7.TempDir($"fromsoft-{key}");
        try
        {
            // the pair plan (every linked stage set: Settings.MaximumPlans), then the app's default on NVIDIA (per-stage cover)
            var pairs = new Planner().Build(game, engine, index, null, Ff7.Nvidia, Path.Combine(dir, "pairs"), null, CancellationToken.None);
            sw.Restart();
            var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, dir, new Progress<string>(output.WriteLine), CancellationToken.None);
            output.WriteLine($"  plan {sw.Elapsed.TotalSeconds:F1}s: {plan.Stats}, platform {plan.Platform}; pair plan {pairs.Stats.Generated} PSOs + {pairs.Stats.D3D11Shaders} D3D11 items");
            output.WriteLine($"  index {index.ContentHash}, plan file sha256 {Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(plan.FilePath)))}, pair plan {Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pairs.FilePath)))}");
            var body = PlanFile.Read(plan.FilePath).Records.ToList();
            var planned = body.Where(r => r.Tag is 'P' or 'S').SelectMany(r => r.Tag == 'P' ? PsoDb.ParseItem(r.Payload).Stages.Values : PsoDb.Parse(r).Stages.Values)
                .Concat(body.Where(r => r.Tag is '1' or '2').SelectMany(r => Rehydrate.References([r]))).ToHashSet();
            output.WriteLine("  shaders in no plan item: " + string.Join(", ", shaders.Where(s => !planned.Contains(s.Sha1)).GroupBy(s => s.Stage).Select(g => $"{g.Count()} {g.Key}")));
            if (api == "D3D11") Assert.True(plan.Stats.D3D11Shaders >= 0.95 * shaders.Count && plan.Stats.Generated == 0);
            else Assert.True(plan.Stats.Generated > 0 && plan.Stats.Uncovered == 0);

            // a sample served by hash, as Materialize asks for them (root signatures come from shaders carrying them)
            var sample = shaders.Where((_, i) => i % 997 == 0).Select(s => s.Sha1).Concat(shaders.Select(s => s.RootSignature).OfType<string>().Take(3)).ToHashSet();
            var got = new Dictionary<string, byte[]>();
            reader.ReadShaders(game, engine, sample, (h, b) => got.Add(h, b), CancellationToken.None);
            Assert.Equal(sample.Order(), got.Keys.Order());
            Assert.All(got, g => Assert.Equal(g.Key, Convert.ToHexStringLower(SHA1.HashData(g.Value))));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A ~1k-PSO sample of a D3D12 title's per-stage plan (every k-th item, the game's own root signatures),
    /// materialized from the install and replayed by scskiller_warm on WARP (the Basic Render Driver: the runtime's
    /// validation on the CPU, no GPU and no GPU driver cache). Manual: SCSKILLER_WARP_TESTS=1 and this checkout's proxy built;
    /// SCSKILLER_WARP_ALL=1 replays the whole plan.
    /// The work folder holds game shader bytes and is deleted.</summary>
    [Theory]
    [InlineData("ER")]
    [InlineData("NR")]
    public void SampleReplaysOnWarp(string key)
    {
        var (game, _, _, _) = Games[key];
        var warm = Path.Combine(Ff7.ProxyBin, "scskiller_warm.exe");
        if (Environment.GetEnvironmentVariable("SCSKILLER_WARP_TESTS") != "1" || !File.Exists(game.ExePath) || !File.Exists(warm)) return;
        var reader = new FromSoftReader(Ff7.TempDir($"fromsoft-warp-data-{key}"));
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var dir = Ff7.TempDir($"fromsoft-warp-{key}");
        try
        {
            var planner = new Planner();
            var full = planner.Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, dir, null, CancellationToken.None);
            var body = PlanFile.Read(full.FilePath).Records.ToList();
            // per-stage plans are whole synthesized PSOs ('S'); pair plans are 'P' items on shared templates
            var all = body.Where(r => r.Tag is 'P' or 'S').ToList();
            var step = Environment.GetEnvironmentVariable("SCSKILLER_WARP_ALL") == "1" ? 1 : Math.Max(1, all.Count / 1000);
            var items = all.Where((_, i) => i % step == 0).ToList();
            Assert.NotEmpty(items);
            var used = items.Where(i => i.Tag == 'P').Select(i => PsoDb.ParseItem(i.Payload).Template).ToHashSet();
            var plan = full with { FilePath = Path.Combine(dir, "sample.bin") };
            PlanFile.Write(plan, body.Where(r => r.Tag == 'B' || r.Tag is 'S' or 'G' or 'C' && used.Contains(r.Key)).Concat(items));
            var work = Path.Combine(dir, "work");
            planner.Materialize(plan, game, engine, reader, null, work, CancellationToken.None);
            Ff7.CheckWarmReady(work);

            var psi = new ProcessStartInfo(warm, [work, $"scsk_fromsoft_warp_{Random.Shared.Next(100000, 999999)}.exe", "--threads", "8", "--adapter-luid", WarpLuid().ToString("x")])
                { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, UseShellExecute = false };
            string o;
            using (var p = Process.Start(psi)!) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
            var done = o.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
                .LastOrDefault(e => e.GetProperty("event").GetString() == "done");
            Assert.True(done.ValueKind == System.Text.Json.JsonValueKind.Object, o);
            var log = Path.Combine(TestEnv.WarmStage(o, work), "scskiller.log");
            var start = o.Split('\n').FirstOrDefault(l => l.Contains("\"start\""))?.Trim();
            Assert.Contains("Basic Render", start);
            output.WriteLine($"{game.Name}: {items.Count} of {all.Count} plan items: {start} {done}\n"
                + (File.Exists(log) ? string.Join('\n', File.ReadLines(log).Where(l => l.Contains("failed")).Take(20)) : ""));
            Assert.Equal(0, done.GetProperty("failed").GetInt64());
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The Basic Render Driver's LUID (the software adapter DXGI lists), as scskiller_warm's --adapter-luid takes it.</summary>
    internal static unsafe long WarpLuid()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
        Assert.True(CreateDXGIFactory1(&iid, out var factory) >= 0);
        var enumAdapters1 = (delegate* unmanaged<nint, uint, nint*, int>)(*(nint**)factory)[12];
        nint adapter;
        for (uint i = 0; enumAdapters1(factory, i, &adapter) >= 0; i++)
        {
            var d = stackalloc byte[312]; // DXGI_ADAPTER_DESC1: Description[128] chars, 4 u32, 3 size_t, LUID, Flags
            ((delegate* unmanaged<nint, byte*, int>)(*(nint**)adapter)[10])(adapter, d);   // GetDesc1
            ((delegate* unmanaged<nint, uint>)(*(nint**)adapter)[2])(adapter);
            if ((*(uint*)(d + 304) & 2) != 0) return ((long)*(int*)(d + 300) << 32) | *(uint*)(d + 296);   // DXGI_ADAPTER_FLAG_SOFTWARE
        }
        throw new InvalidOperationException("no software adapter");
    }

    [System.Runtime.InteropServices.DllImport("dxgi.dll")] static extern unsafe int CreateDXGIFactory1(Guid* riid, out nint factory);

    sealed class Stub : IEngineReader
    {
        public EngineInfo? Detect(Game game) => null;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => throw new NotSupportedException();
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) => throw new NotSupportedException();
    }
}
