using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;

namespace SCSKiller.Ui.Models;

/// <summary>One row of THIRD-PARTY-NOTICES.md's component table, with its licence text (empty when the notices have none).</summary>
public sealed record Notice(string Name, string Version, string Licence, string Copyright, string Project, string Text, string Note)
{
    public string Header => $"{Name} · {Version}";
    /// <summary>The SPDX id, or what a LicenseRef / NOASSERTION means in words.</summary>
    public string LicenceName => Licence switch
    {
        "NOASSERTION" => "No licence stated",
        _ when Licence.StartsWith("LicenseRef-Microsoft") => "Microsoft licence",
        _ when Licence.EndsWith("Proprietary") => "Proprietary",
        _ => Licence,
    };
    public string Detail => $"{LicenceName} · {Copyright}";
    public bool HasNote => Note.Length > 0;
    public string Body => Text.Length > 0 ? Text : "No licence text is published with it.";
}

public sealed class AboutVm : Bindable
{
    static readonly string Build = "Version " + (typeof(AboutVm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?");
    public string Version => $"{Build} · {(UpdatedElsewhere ? UpdateChannels.Shown(AppVersion.Current.Channel) : UpdateChannels.Shown(Host.Core.Settings.UpdateChannel, AppVersion.Current.Channel, Host.Account?.Status?.Ent, Host.Account?.SignedIn == true))}";

    // "Download source code": stable opens the public repo's tag; alpha and beta
    // fetch the source zip served next to the package (with the access token) into Downloads; none on internal/dev builds.
    public bool ShowsSource { get; } = UpdateFeeds.Source(AppVersion.Current) != null && (AppVersion.Current.Channel == UpdateChannels.Stable || Host.Account?.SignedIn == true);
    public bool SourceIdle { get; private set; } = true;
    public string SourceStatus { get; private set; } = "";

    public async Task GetSourceAsync()
    {
        var v = AppVersion.Current;
        var url = UpdateFeeds.Source(v)!;
        if (v.Channel == UpdateChannels.Stable)
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return;
        }
        (SourceIdle, SourceStatus) = (false, "Downloading…");
        Changed();
        try
        {
            using var http = new HttpClient(RouteFailover.Default, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new("Bearer", (Host.Account is { } account ? await account.GetAccessTokenAsync() : null) ?? throw new InvalidOperationException("sign in with Patreon first"));
            using var r = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            r.EnsureSuccessStatusCode();
            var file = Path.Combine(DownloadsFolder(), $"SCSKiller-source-{v}.zip");
            await using (var f = File.Create(file + ".partial")) await r.Content.CopyToAsync(f);
            File.Move(file + ".partial", file, overwrite: true);
            SourceStatus = "Saved to " + file;
        }
        catch (Exception e) { SourceStatus = "Couldn't download it: " + e.Message; }
        SourceIdle = true;
        Changed();
    }

    /// <summary>The user's Downloads folder: the shell's known folder on Windows (it can be moved or redirected), XDG's
    /// elsewhere.</summary>
    public static string DownloadsFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            if (SHGetKnownFolderPath(FolderDownloads, 0, 0, out var p) != 0) return Path.Combine(home, "Downloads");
            try { return Marshal.PtrToStringUni(p)!; }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".config");
        var dirs = Path.Combine(config, "user-dirs.dirs");
        return XdgDownloads(File.Exists(dirs) ? File.ReadAllText(dirs) : null, home);
    }

    /// <summary>XDG_DOWNLOAD_DIR of a user-dirs.dirs file (a quoted path, absolute or $HOME/…), else ~/Downloads.</summary>
    public static string XdgDownloads(string? userDirs, string home)
    {
        var line = userDirs?.Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.StartsWith("XDG_DOWNLOAD_DIR="));
        var value = line?["XDG_DOWNLOAD_DIR=".Length..].Trim();
        if (value is ['"', .., '"']) value = value[1..^1];
        if (value?.StartsWith("$HOME") == true) value = home + value[5..];
        if (value != null) value = Regex.Replace(value, @"\\(.)", "$1");   // shell quoting: \$ is a $
        return value is { Length: > 0 } && Path.IsPathRooted(value) ? value : Path.Combine(home, "Downloads");
    }

    static readonly Guid FolderDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);

    // The app sets Updater.Host once it runs the updater: until then, nothing reads the state that needs it.
    static bool Wired => Updater.Host.Core != null;

    // "Check for updates": every build Velopack installed (the design data shows it too); the state of the update next to it.
    // Installed builds a Velopack manager, which needs the locator the app's start sets: the design data never asks.
    public bool ShowsCheck { get; } = Host.Design != null || (Wired && Updater.Installed);
    /// <summary>A Flatpak's or a package's line in place of the check.</summary>
    public string? UpdatesFrom { get; } = Wired ? Updater.Installation.UpdatesFrom : null;
    public bool UpdatedElsewhere => UpdatesFrom != null;
    public bool ShowsAutoInstall => ShowsCheck && (!Wired || Updater.Channel != UpdateChannels.Internal);
    public bool CanCheck => !Updater.Checking && !Updater.Restarting;
    public string UpdateNote => Updater.Downloading is { } d ? $"Downloading SCSKiller {d}…"
        : Updater.Checking ? "Checking for updates…"
        : Wired && Updater.Ready is { } v ? AutoInstall.ReadyNote(v, Updater.InstallsAutomatically)
        : Updater.UpToDateOn is { } c ? UpdateChannels.UpToDate(c, Host.Core.Settings.UpdateChannel, AppVersion.Current.Channel) : "";
    public string? UpdateProblem => Updater.Problem;
    public bool HasUpdateProblem => Updater.Problem != null;

    // Checking and downloading never wait for the queue or an offline session: only applying does (Updater.ApplyAsync).
    public void CheckForUpdates() { if (Wired && !Updater.Restarting) _ = Updater.CheckNowAsync(); }

    public bool? InstallUpdatesAutomatically
    {
        get => Host.Core.Settings.InstallUpdatesAutomatically;
        set
        {
            if (value is not { } v || v == Host.Core.Settings.InstallUpdatesAutomatically) return;
            Host.Core.Settings = Host.Core.Settings with { InstallUpdatesAutomatically = v };
            Changed();   // the note says when it installs
        }
    }

    readonly Coalesced changed;
    /// <summary>While the page is shown.</summary>
    public void Watch(bool on)
    {
        Updater.Changed -= changed.Request;
        Host.Account?.Changed -= changed.Request;
        if (!on) return;
        Updater.Changed += changed.Request;
        Host.Account?.Changed += changed.Request;   // the channel line follows a sign-in
        try { Problems = Host.Core.ProblemsSent(); }
        catch (Exception) { }   // unreadable: the list stays empty
        Changed();
    }

    public IReadOnlyList<SentProblem> Problems { get; private set; } = [];
    public bool NoProblems => Problems.Count == 0;

    /// <summary>The What's new dialog's lines: a heading in bold, a bullet's bold, code and link spans after a bullet mark.</summary>
    public static List<(bool Heading, List<Span> Spans, bool Group)> WhatsNewLines(WhatsNewSection section) =>
        section.Lines.Select(l => (l.Heading, l.Heading ? new List<Span> { new(l.Text, SpanKind.Bold) } : WhatsNew.Spans(l.Text).Prepend(new Span("• ")).ToList(), l.Group)).ToList();

    public List<Notice> Components { get; }
    public string Trademarks { get; }

    /// <summary><paramref name="notices"/>: THIRD-PARTY-NOTICES.md, which the app embeds.</summary>
    public AboutVm(string notices)
    {
        changed = new(Host.Ui, Changed);
        (Components, Trademarks) = Parse(notices);
        if (!OperatingSystem.IsWindows()) Components.RemoveAll(c => WindowsOnly.Any(c.Name.StartsWith));
    }

    // the components only the Windows package ships
    static readonly string[] WindowsOnly = ["Windows App SDK", "Microsoft.Windows.SDK.NET", "ANGLE"];

    /// <summary>The "## Components" table (the last cell links a "### Ln" licence section, whose first ```text block is the
    /// text) and the "## Trademarks" bullets.</summary>
    public static (List<Notice>, string) Parse(string md)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n');
        string Section(string title) => string.Join('\n', lines.SkipWhile(l => l != "## " + title).Skip(1).TakeWhile(l => !l.StartsWith("## ")));
        var texts = new Dictionary<string, string>();
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("### L"))
            {
                var start = Array.IndexOf(lines, "```text", i) + 1;
                texts[lines[i][4..].Trim()] = string.Join('\n', lines[start..Array.IndexOf(lines, "```", start)]);
            }
        // the notes under the table: "- **Name**: note"
        var notes = Section("Components").Split('\n').Where(l => l.StartsWith("- **") && l.Contains("**: "))
            .ToDictionary(l => l[4..l.IndexOf("**: ")], l => l[(l.IndexOf("**: ") + 4)..].Replace("`", ""));
        var rows = Section("Components").Split('\n').Where(l => l.StartsWith("| ") && !l.StartsWith("| Component"))
            .Select(l => l.Trim('|').Split(" | ").Select(c => c.Trim()).ToArray())
            .Select(c => new Notice(c[0], c[0].StartsWith(".NET runtime") ? Environment.Version.ToString() : c[1], c[2], c[3], c[4], c[5].StartsWith('[') ? texts.GetValueOrDefault(c[5][1..c[5].IndexOf(']')], "") : "",
                notes.GetValueOrDefault(c[0], "")))
            .ToList();
        var marks = string.Join('\n', Section("Trademarks").Split('\n').Where(l => l.StartsWith("- ")).Select(l => l[2..]));
        return (rows, marks);
    }
}
