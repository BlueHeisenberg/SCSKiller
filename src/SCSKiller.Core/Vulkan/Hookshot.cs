using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Vulkan;

/// <summary>A HookshotBuilder port (<see cref="CarvedReader.Hookshot"/>) compiles through its own precompile: hookshot.exe
/// started hidden with RX_VK_PRECOMPILE=1 translates every shader of its census, warms the driver with its compute pipelines,
/// and builds every graphics pipeline the player's own hookshot_pipelines.bin notes, in its own process, so the driver cache
/// it fills is the one the game reads. The plan is that one run, a single Vulkan item.</summary>
public static partial class Hookshot
{
    public const string ReadyReason = "compiles through the port's own shader precompile";
    public const string WindowsOnly = "a Hookshot port compiles on Windows only for now";
    const string NotesFile = "hookshot_pipelines.bin";
    const string ItemName = "hookshot-precompile";

    public static bool Is(Game g) => CarvedReader.Hookshot(g.ExePath);

    public static PlanCheck Check() => OperatingSystem.IsWindows() ? new(Readiness.Ready, ReadyReason) : new(Readiness.Unsupported, WindowsOnly);

    public static Plan Build(Game game, VendorCaps caps, string outDir)
    {
        var plan = new Plan(game.Id, ShaderIndex.EmptyContentHash, VulkanPlan.Platform, caps.Profile, new PlanStats(0, 1, 0, 0, false), Path.Combine(outDir, "plan.bin"));
        PlanFile.Write(plan, [VulkanPlan.Item(ItemName)]);
        return plan;
    }

    public static void Materialize(string workDir)
    {
        Directory.CreateDirectory(workDir);
        File.WriteAllText(Path.Combine(workDir, Planner.SkippedFile), "0 0");
    }

    /// <summary>The hidden precompile run of the game's own exe (Windows: Hookshot.Windows.cs).</summary>
    public static IWarmRun Start(Game game, string workDir, GpuInfo gpu, IProgress<WarmProgress>? progress, IProgress<string>? log)
    {
        IWarmRun? run = null;
        StartRun(game, workDir, gpu, progress, log, ref run);
        return run ?? throw new PlatformNotSupportedException(WindowsOnly);
    }

    static partial void StartRun(Game game, string workDir, GpuInfo gpu, IProgress<WarmProgress>? progress, IProgress<string>? log, ref IWarmRun? run);

    /// <summary>At SCSKiller's start: the crash markers of a hidden run whose SCSKiller died with it (Hookshot.Windows.cs).</summary>
    public static void Recover(string gamesDir, IProgress<string>? log) => RecoverAll(gamesDir, log);

    static partial void RecoverAll(string gamesDir, IProgress<string>? log);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> Running = new();

    /// <summary>A hookshot.exe a precompile run of this process started: SCSKiller's own, never the player's game.</summary>
    public static bool Ours(int pid) => Running.ContainsKey(pid);

    static string TranslatingNote(TimeSpan t, double? share = null) => $"translating Hookshot's shaders, {(share is { } f ? $"{f * 100:0}%, " : "")}{ScsKiller.Duration(t)}";
    static string SavingNote(TimeSpan t) => $"letting Hookshot save its pipelines, {ScsKiller.Duration(t)}";
}
