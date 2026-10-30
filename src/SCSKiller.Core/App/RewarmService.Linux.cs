using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SCSKiller.Core.App;

/// <summary>The Linux "re-warm after a driver update" of the Ask and When idle modes, as <see cref="ScheduledTask"/> on Windows:
/// a systemd user timer that runs <c>rewarm-stale --if-driver-changed</c> two minutes after the user manager starts (at
/// sign-in, a desktop's or Steam's Game Mode, which runs no XDG autostart) and again an hour after each run. The program
/// is the app's (<see cref="LinuxStartup.Program"/>), an AppImage's own file and never a path in its mount, with
/// <see cref="CliArg"/>: the app hands the rest to the CLI beside it. Only files carrying <see cref="OwnKey"/> are ever
/// changed or removed.</summary>
public static class RewarmService
{
    public const string Name = "scskiller-rewarm", Service = Name + ".service", Timer = Name + ".timer";
    /// <summary>The app's argument that runs the CLI beside it in its place, and that CLI's file name.</summary>
    public const string CliArg = "--cli", Cli = "scskiller";
    const string OwnKey = "X-SCSKiller", Command = "rewarm-stale --if-driver-changed";

    /// <summary>systemctl --user, or a fake in tests.</summary>
    public interface ISystemctl
    {
        /// <summary>systemctl --user with <paramref name="args"/>: its exit code and what it wrote to stderr.</summary>
        (int Code, string Error) Run(params string[] args);
    }

    /// <summary>$XDG_CONFIG_HOME/systemd/user, the user manager's own unit folder.</summary>
    public static string UnitDir(Func<string, string?> env) => Path.Combine(LinuxStartup.ConfigHome(env), "systemd", "user");

    /// <summary>The service the timer starts. ConditionACPower and Nice as the task's battery rules and priority 7; the CLI
    /// lowers itself further for a compile. KillMode=mixed: a stop reaches the CLI alone, which stops the warm so the driver
    /// writes its cache.</summary>
    public static string ServiceText(string program) =>
        $"""
        [Unit]
        Description=SCSKiller: re-warm game shader caches after a GPU driver update
        ConditionACPower=true
        ConditionFileIsExecutable={program.Replace("%", "%%")}
        {OwnKey}=rewarm

        [Service]
        Type=oneshot
        ExecStart={Quote(program)} {CliArg} {Command}
        Nice=10
        KillMode=mixed

        """;

    public static string TimerText() =>
        $"""
        [Unit]
        Description=SCSKiller: check for a GPU driver update after sign-in
        {OwnKey}=rewarm

        [Timer]
        OnStartupSec=2min
        OnUnitInactiveSec=1h

        [Install]
        WantedBy=timers.target

        """;

    /// <summary>The quoted program of ExecStart: C escapes for \ and ", and %% for the specifiers systemd expands. Its first
    /// word has no $ expansion.</summary>
    static string Quote(string program)
    {
        var q = new StringBuilder("\"");
        foreach (var c in program)
            _ = c switch
            {
                '\\' or '"' => q.Append('\\').Append(c),
                '%' => q.Append("%%"),
                _ => q.Append(c),
            };
        return q.Append('"').ToString();
    }

    static string Unquote(string word)
    {
        var s = new StringBuilder();
        for (var i = 1; i < word.Length - 1; i++)
            s.Append(word[i] is '\\' or '%' ? word[++i] : word[i]);
        return s.ToString();
    }

    /// <summary>A unit file as read: SCSKiller wrote it, and for the service the program it runs.</summary>
    sealed record Unit(string Text, bool Ours, string? Program);

    static Unit? Read(string file)
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new FileInfo(file).LinkTarget == null ? null : new("", false, null);   // a link to nothing is another's
        }
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        var exec = lines.FirstOrDefault(l => l.StartsWith("ExecStart=\"", StringComparison.Ordinal)) is { } line ? line["ExecStart=".Length..] : null;
        var end = exec == null ? -1 : QuoteEnd(exec);
        return new(text, lines.Contains($"{OwnKey}=rewarm"), end > 0 ? Unquote(exec![..(end + 1)]) : null);
    }

    static int QuoteEnd(string word)
    {
        for (var i = 1; i < word.Length; i++)
            if (word[i] == '\\') i++;
            else if (word[i] == '"') return i;
        return -1;
    }

    /// <summary>Writes both units and enables the timer (from the next sign-in, as the task registers without running).
    /// Throws when a file of the same name isn't ours, or systemctl fails.</summary>
    public static void Enable(string program, string dir, ISystemctl systemctl)
    {
        var units = new[] { (Service, ServiceText(program)), (Timer, TimerText()) }.Select(u => (File: Path.Combine(dir, u.Item1), Text: u.Item2)).ToList();
        foreach (var u in units)
            if (Read(u.File) is { Ours: false }) throw new InvalidOperationException($"{u.File} isn't SCSKiller's");
        Directory.CreateDirectory(dir);
        foreach (var u in units) MenuEntry.Write(u.File, null, u.Text);
        Run(systemctl, "daemon-reload");
        Run(systemctl, "enable", Timer);
    }

    /// <summary>Stops and disables the timer and removes our units; a compile the service runs goes on, as a removed task's
    /// does. Nothing of ours there: nothing runs.</summary>
    public static void Disable(string dir, ISystemctl systemctl)
    {
        var ours = new[] { Service, Timer }.Select(n => Path.Combine(dir, n)).Where(f => Read(f) is { Ours: true }).ToList();
        if (ours.Count == 0) return;
        if (ours.Contains(Path.Combine(dir, Timer))) Run(systemctl, "disable", "--now", Timer);
        foreach (var f in ours) File.Delete(f);
        Run(systemctl, "daemon-reload");
    }

    /// <summary>App start: our service follows this copy, as <see cref="LinuxStartup.Reconcile"/>: rewritten when it runs
    /// <paramref name="program"/> with other text, or when an <paramref name="installed"/> copy finds the program it runs
    /// gone (an AppImage moved). Removed when it runs <paramref name="program"/> and the CLI is refused
    /// (<paramref name="cliRefused"/>). Never created here. True if it changed.</summary>
    public static bool Reconcile(string program, string? cliRefused, bool installed, string dir, ISystemctl systemctl, Func<string, bool> exists)
    {
        var file = Path.Combine(dir, Service);
        if (Read(file) is not { Ours: true } now || Read(Path.Combine(dir, Timer)) is not { Ours: true }) return false;
        if (cliRefused != null)
        {
            if (now.Program != program) return false;
            Disable(dir, systemctl);
            return true;
        }
        var text = ServiceText(program);
        if (now.Text == text || now.Program != program && !(installed && (now.Program is not { } other || !exists(other)))) return false;
        MenuEntry.Write(file, now.Text, text);
        Run(systemctl, "daemon-reload");
        return true;
    }

    /// <summary>Settings' driver-update choice on Linux (<see cref="ScsKiller.ApplyDriverUpdateMode"/>): Ask and When idle enable
    /// the service (Ask's run starts the app, which notifies), Off removes it. A Flatpak can't add a user service: there Ask
    /// is the sign-in start's alone. Throws, with what to tell the user, when it can't be set up: no <paramref name="program"/>
    /// that may start at sign-in (<paramref name="refused"/> says why), or a CLI that may not run (<paramref name="cliRefused"/>).</summary>
    public static void Apply(DriverUpdateMode mode, bool flatpak, string? program, string? refused, string? cliRefused, string dir, ISystemctl systemctl)
    {
        if (flatpak)
        {
            if (mode == DriverUpdateMode.WhenIdle) throw new InvalidOperationException("the Flatpak can't add the background service");
            return;
        }
        if (mode == DriverUpdateMode.Off) Disable(dir, systemctl);
        else if (program == null) throw new InvalidOperationException(refused ?? "this copy's file isn't known");
        else if (cliRefused != null) throw new InvalidOperationException(cliRefused);
        else Enable(program, dir, systemctl);
    }

    /// <summary>Why this copy's CLI (<see cref="CliRefused"/>) can't be what the service runs through
    /// <paramref name="program"/>; null when it can.</summary>
    public static string? ThisCliRefused(string program) => CliRefused(Path.Combine(AppContext.BaseDirectory, Cli), program, Environment.ProcessPath,
        Libc.RealPath, Environment.GetEnvironmentVariable, Libc.UserId, Libc.Lstat, Libc.AccessAcl);

    /// <summary>Why <paramref name="cli"/>, the CLI beside this process's file, can't be what the service runs through
    /// <paramref name="program"/> (<see cref="ServiceText"/>: the program, then <see cref="CliArg"/>); null when it can.
    /// When <paramref name="program"/> is this process's own file (an unpacked copy, a package), that file execs the CLI
    /// beside it, so the CLI takes the app's guard (<see cref="MenuEntry.Refused"/>) too: its folder or a link may let
    /// another user replace it. Any other <paramref name="program"/> is an AppImage's file, the one file the service runs,
    /// which took that guard already (<see cref="LinuxStartup.Target"/>): it starts the CLI from its own image, and
    /// nothing in this process's mount is checked or trusted.</summary>
    internal static string? CliRefused(string cli, string program, string? processPath, Func<string, string?> realPath, Func<string, string?> env, uint uid,
        Func<string, (uint Uid, uint Mode)?> lstat, Func<string, int> acl) =>
        processPath != null && realPath(processPath) is { } self && self != program ? null
        : lstat(cli) == null ? $"the command-line tool ({Cli}) is not next to the app"
        : MenuEntry.Refused(cli, env, uid, lstat, acl) is { } why ? $"the command-line tool: {why}" : null;

    static void Run(ISystemctl systemctl, params string[] args)
    {
        var (code, error) = systemctl.Run(args);
        if (code != 0) throw new InvalidOperationException($"systemctl --user {string.Join(' ', args)} failed ({code}): {error}");
    }

    /// <summary>The real systemctl --user, when the user manager runs (its private socket in XDG_RUNTIME_DIR).</summary>
    public sealed class Systemctl : ISystemctl
    {
        public (int Code, string Error) Run(params string[] args)
        {
            if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is not { Length: > 0 } runtime || !Path.Exists(Path.Combine(runtime, "systemd", "private")))
                throw new InvalidOperationException("systemd's user manager isn't running");
            var psi = new ProcessStartInfo("systemctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--user");
            foreach (var a in args) psi.ArgumentList.Add(a);
            try
            {
                using var p = Process.Start(psi)!;
                _ = p.StandardOutput.ReadToEndAsync();
                var error = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(TimeSpan.FromSeconds(30)))
                {
                    p.Kill();
                    return (-1, "no answer in 30 s");
                }
                return (p.ExitCode, error.Result.Trim());
            }
            catch (Win32Exception) { throw new InvalidOperationException("systemctl isn't installed"); }
        }
    }
}
