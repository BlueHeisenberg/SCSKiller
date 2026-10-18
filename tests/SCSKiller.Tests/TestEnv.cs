using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Tests;

/// <summary>Where the machine-dependent tests find things, so no test carries a machine path.
/// Games: the folders in SCSKILLER_TEST_GAMES_ROOT (';'-separated folders that hold game install folders, like
/// steamapps\common), then every Steam library's steamapps\common (Steam's libraryfolders.vdf), then each fixed drive's
/// XboxGames. Dev data (the GPU lock, recordings): SCSKILLER_DEV_DIR; the GPU is also busy while the file named by
/// SCSKILLER_GPU_BUSY_FILE exists. A development tree may set both defaults in a partial of its own; elsewhere the dev
/// folder is %TEMP%\scskiller-test and nothing else marks the GPU busy.</summary>
static partial class TestEnv
{
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

    /// <summary>The codecs the readers load come from the build's cache, as build\publish.ps1's do, never the app's data folder.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void BuildCodecs() => SCSKiller.Core.App.Codecs.DirOverride =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller-build", "codecs");

    /// <summary>This run's own Busy and CacheDelete mutex names, for the processes the tests start too: an app running in the
    /// same logon session sees none of the run's holds.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void MutexScope()
    {
        var scope = ".test-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("SCSKILLER_TEST_SCOPE", scope);
        SCSKiller.Core.App.Busy.Scope = scope;
    }

    /// <summary>The recorder folder the tests stamp with (its recorder.key, recorder.revoked and refused\) and
    /// the built proxy and selftest read (SCSKILLER_TEST_RECORDER_DIR, inherited by every process a test starts), never the
    /// user's %LOCALAPPDATA%\SCSKiller.</summary>
    public static readonly string Recorder = Path.Combine(Path.GetTempPath(), "scskiller-test-recorder-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>The user's live recorder folder: read, never written.</summary>
    public static readonly string LiveRecorder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller");

    static readonly bool LiveKeyExisted = File.Exists(Path.Combine(LiveRecorder, "recorder.key")), LiveLedgerExisted = Directory.Exists(Path.Combine(LiveRecorder, "armed"));

    /// <summary>Fails when the tests reached <see cref="LiveRecorder"/>: a refusal left there for one of <paramref name="keys"/>
    /// (the file keys of a test's exes), a recorder.key made, or the user's 1.2 ledger deleted during the run.</summary>
    public static void AssertLiveRecorderUntouched(IReadOnlySet<string> keys)
    {
        Assert.True(LiveKeyExisted || !File.Exists(Path.Combine(LiveRecorder, "recorder.key")), "a recorder.key was made in the live folder during the run");
        Assert.True(!LiveLedgerExisted || Directory.Exists(Path.Combine(LiveRecorder, "armed")), "the live 1.2 ledger was deleted during the run");
        Assert.DoesNotContain(keys, k => File.Exists(Path.Combine(LiveRecorder, "refused", k)));
    }

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void TestRecorder()
    {
        Environment.SetEnvironmentVariable("SCSKILLER_TEST_RECORDER_DIR", Recorder);
        SCSKiller.Core.App.ScsKiller.RecorderDir = Recorder;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Recorder, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        };
    }

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

    /// <summary>The skip reason when the test output has no Fixtures\<paramref name="folder"/>: the fixtures taken from games
    /// stay out of the public source.</summary>
    public static string? FixtureMissing(string folder) =>
        Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder)) ? null : $@"no Fixtures\{folder}: kept private";
}

/// <summary>Skipped without its fixture folder (<see cref="TestEnv.FixtureMissing"/>).</summary>
public sealed class FixtureFactAttribute : FactAttribute
{
    public FixtureFactAttribute(string folder) => Skip = TestEnv.FixtureMissing(folder);
}

/// <summary>Skipped without its fixture folder (<see cref="TestEnv.FixtureMissing"/>).</summary>
public sealed class FixtureTheoryAttribute : TheoryAttribute
{
    public FixtureTheoryAttribute(string folder) => Skip = TestEnv.FixtureMissing(folder);
}
