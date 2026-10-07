using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Tests;

/// <summary>Where the machine-dependent tests find things, so no test carries a machine path.
/// Games: the folders in SCSKILLER_TEST_GAMES_ROOT (';'-separated folders that hold game install folders, like
/// steamapps\common), then every Steam library's steamapps\common (Steam's libraryfolders.vdf), then each fixed drive's
/// XboxGames. Dev data (the GPU lock, recordings): SCSKILLER_DEV_DIR; the GPU is also busy while the file named by
/// SCSKILLER_GPU_BUSY_FILE exists. A development tree may set both defaults in TestEnv.Private.cs; elsewhere the dev
/// folder is %TEMP%\scskiller-test and nothing else marks the GPU busy.</summary>
static partial class TestEnv
{
    /// <summary>Core's display text follows the UI culture (Loc), and the build ships the zh-Hans resources: the tests
    /// expect the English whatever the machine's display language. A test that needs another culture sets its own.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void EnglishDisplayText() =>
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

    static partial void TeamDefaults(ref string? devDir, ref string? gpuBusyFile);

    static readonly string? BusyFile;

    /// <summary>The dev data folder: the GPU lock, local recordings (e.g. ff7\).</summary>
    public static readonly string DevDir;

    /// <summary>One heavy GPU run at a time: the team's lock file, deleted when done, ignored when older than 30 min.</summary>
    public static string GpuLockPath => Path.Combine(DevDir, "gpu.lock");

    /// <summary>Another tool's GPU lock exists: measure nothing.</summary>
    public static bool GpuBusyElsewhere => BusyFile != null && File.Exists(BusyFile);

    /// <summary>A scskiller_warm run's own staging folder (its log and outputs), from the stage event in its stdout; an older
    /// warmer's is <paramref name="work"/>\stage, as the app takes it.</summary>
    public static string WarmStage(string stdout, string work) =>
        stdout.Split('\n').Select(l => SCSKiller.Core.Warming.WarmEvent.Parse(l.Trim())).FirstOrDefault(e => e?.Event == "stage")?.Stage
        ?? Path.Combine(work, "stage");

    static TestEnv()
    {
        string? dev = Env("SCSKILLER_DEV_DIR"), busy = Env("SCSKILLER_GPU_BUSY_FILE");
        TeamDefaults(ref dev, ref busy);
        DevDir = dev ?? Path.Combine(Path.GetTempPath(), "scskiller-test");
        BusyFile = busy;
    }

    static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <summary>SCSKILLER_TEST_GAMES_ROOT's folders, then Steam's libraries' steamapps\common, existing ones only.</summary>
    public static readonly IReadOnlyList<string> SteamCommon = FindSteamCommon();

    /// <summary><see cref="SteamCommon"/>, then each fixed drive's XboxGames folder, then the folders GOG installs are in.</summary>
    public static readonly IReadOnlyList<string> GameRoots = SteamCommon
        .Concat(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => Path.Combine(d.Name, "XboxGames")).Where(Directory.Exists))
        .Concat(GogParents()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static IEnumerable<string> GogParents()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        return root == null ? [] : root.GetSubKeyNames()
            .Select(id => { using var k = root.OpenSubKey(id); return k?.GetValue("path") as string; })
            .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Select(p => Path.GetDirectoryName(p!.TrimEnd('\\'))!).ToList();
    }

    /// <summary>The first <paramref name="folder"/> (an install folder name, may have subfolders) found under
    /// <see cref="GameRoots"/>; else a path that doesn't exist, so a test that checks for the game skips.</summary>
    public static string GameDir(string folder) =>
        GameRoots.Select(r => Path.Combine(r, folder)).FirstOrDefault(Directory.Exists) ?? Path.Combine(Path.GetTempPath(), "scskiller-no-game", folder);

    static List<string> FindSteamCommon()
    {
        var roots = (Env("SCSKILLER_TEST_GAMES_ROOT") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        try
        {
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ?? @"C:\Program Files (x86)\Steam";
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))   // first: its paths keep the folders' real case (the registry's SteamPath is lower case)
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    roots.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common"));
            roots.Add(Path.Combine(steam, "steamapps", "common"));
        }
        catch (IOException) { }
        return roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The checkout these tests were built from (the folder with SCSKiller.slnx above the test binaries).</summary>
    public static readonly string RepoRoot = FindRepoRoot();

    /// <summary>The main checkout when these tests run from a git worktree (where gitignored data like out\ lives), else
    /// <see cref="RepoRoot"/>. SCSKILLER_MAIN_CHECKOUT overrides.</summary>
    public static readonly string MainCheckout = Env("SCSKILLER_MAIN_CHECKOUT") ?? FindMainCheckout();

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SCSKiller.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }

    static string FindMainCheckout()
    {
        // a worktree's .git is a file: "gitdir: <main>\.git\worktrees\<name>"
        var git = Path.Combine(RepoRoot, ".git");
        if (!File.Exists(git) || File.ReadAllText(git).Trim() is not { } line || !line.StartsWith("gitdir:")) return RepoRoot;
        var gitDir = Path.GetFullPath(Path.Combine(RepoRoot, line["gitdir:".Length..].Trim()));
        var common = Directory.GetParent(gitDir)?.Parent;   // <main>\.git
        return common?.Name == ".git" && common.Parent != null ? common.Parent.FullName : RepoRoot;
    }
}
