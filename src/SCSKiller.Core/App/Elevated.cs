using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace SCSKiller.Core.App;

/// <summary>Admin-only actions (the NVIDIA cache size, Auto Shader Compilation) from an unelevated app: the bundled CLI
/// run with a UAC prompt (<c>runas</c>). runas can't redirect stdout, so the CLI writes its outcome as one JSON line to
/// the file given by <see cref="ResultArg"/>; <see cref="YesArg"/> skips its y/N prompt (the app has already asked).
/// UAC usually elevates the same account (split token); over-the-shoulder elevation runs as another administrator,
/// which <see cref="ForUserArg"/> lets a command detect.</summary>
public static partial class Elevated
{
    public const string YesArg = "--yes", ResultArg = "--result", ForUserArg = "--for-user";
    const int ErrorCancelled = 1223;   // the user said No to UAC

    public sealed record Result(bool Ok, string Message);

    public static partial bool IsAdmin { get; }
    public static partial string CurrentUserSid { get; }

    /// <summary>The process runs elevated. Null: can't tell, or no such thing on this OS. <paramref name="deniedIsElevated"/>
    /// false: a token refused to us is unknown, as anti-cheat refuses it too.</summary>
    public static partial bool? OfProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process, bool deniedIsElevated = true);

    /// <summary>Runs <c>scskiller &lt;args&gt; --yes --result &lt;file&gt;</c> elevated and waits for it. Null = UAC cancelled.</summary>
    public static Result? Run(params string[] args)
    {
        var cli = CliExe() ?? throw new FileNotFoundException(@"the command-line tool (cli\scskiller.exe) is not next to the app");
        var file = Path.Combine(Path.GetTempPath(), $"scskiller-elevated-{Guid.NewGuid():N}.json");
        var psi = new ProcessStartInfo(cli, CommandLine([.. args, YesArg, ResultArg, file]))
            { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        try
        {
            Process p;
            try { p = Process.Start(psi)!; }
            catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled) { return null; }
            using (p) { p.WaitForExit(); return ReadResult(file, p.ExitCode); }
        }
        finally { File.Delete(file); }
    }

    /// <summary>The CLI's outcome: its JSON line, else what the exit code says.</summary>
    public static Result ReadResult(string file, int exitCode)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Result>(File.ReadAllText(file)) is { } r) return r;
        }
        catch (JsonException) { }
        return new(exitCode == 0, exitCode == 0 ? "" : $"the command-line tool failed (exit code {exitCode})");
    }

    public static void WriteResult(string file, bool ok, string message) =>
        File.WriteAllText(file, JsonSerializer.Serialize(new Result(ok, message)) + Environment.NewLine);

    /// <summary>A y/N answer piped to the CLI, trimmed; null = none (end of input). Read as UTF-8 with any byte-order mark
    /// dropped: Windows PowerShell 5.1 can send one before the text, which the console's code page would keep as "∩╗┐".</summary>
    public static string? ReadAnswer(Stream input) => new StreamReader(input, System.Text.Encoding.UTF8, true).ReadLine()?.Trim();

    /// <summary>ShellExecute takes one string: quote what has spaces (none of our arguments contain quotes).</summary>
    public static string CommandLine(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));

    /// <summary>The published app's cli\scskiller.exe, else (dev tree) the newest build under src\SCSKiller.Cli\bin.</summary>
    public static string? CliExe(string? baseDir = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        var published = Path.Combine(baseDir, "cli", "scskiller.exe");
        if (File.Exists(published)) return published;
        for (var d = new DirectoryInfo(baseDir); d != null; d = d.Parent)
        {
            var bin = Path.Combine(d.FullName, "src", "SCSKiller.Cli", "bin");
            if (Directory.Exists(bin))
                return new DirectoryInfo(bin).EnumerateFiles("scskiller.exe", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        return null;
    }
}
