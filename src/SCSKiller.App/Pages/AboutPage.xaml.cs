using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using SCSKiller.Core.App;

namespace SCSKiller.App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutVm Vm { get; } = new();
    public AboutPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Vm.Watch(true);
        Unloaded += (_, _) => Vm.Watch(false);
    }

    // Checking and downloading never wait for the queue or an offline session: only applying does (Updater.ApplyAsync).
    void OnCheckForUpdates(object _, Microsoft.UI.Xaml.RoutedEventArgs __) { if (!Updater.Restarting) _ = Updater.CheckNowAsync(); }

    async void OnSource(object _, Microsoft.UI.Xaml.RoutedEventArgs __) => await Vm.GetSourceAsync();

    /// <summary>--screenshots: the trademarks open, or the first component's licence (scrolled to it).</summary>
    public void ShowExpanded(bool licence)
    {
        TrademarksBox.IsExpanded = !licence;
        if (licence && Components.ContainerFromIndex(0) is Microsoft.UI.Xaml.DependencyObject c
            && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(c) > 0 && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(c, 0) is Expander e)
        {
            e.IsExpanded = true;
            e.StartBringIntoView(new() { VerticalAlignmentRatio = 0 });
        }
    }
}

/// <summary>One row of THIRD-PARTY-NOTICES.md's component table, with its licence text (empty when the notices have none).</summary>
public sealed record Notice(string Name, string Version, string Licence, string Copyright, string Project, string Text, string Note)
{
    public string Header => $"{Name} · {Version}";
    /// <summary>The SPDX id, or what a LicenseRef / NOASSERTION means in words.</summary>
    public string LicenceName => Licence switch
    {
        "NOASSERTION" => Loc.Text("No licence stated"),
        _ when Licence.StartsWith("LicenseRef-Microsoft") => Loc.Text("Microsoft licence"),
        _ when Licence.EndsWith("Proprietary") => "Proprietary",
        _ => Licence,
    };
    public string Detail => $"{LicenceName} · {Copyright}";
    public bool HasNote => Note.Length > 0;
    public string Body => Text.Length > 0 ? Text : Loc.Text("No licence text is published with it.");
}

public sealed class AboutVm : Bindable
{
    public string Version { get; } = Loc.Text("Version ") + (typeof(AboutVm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")
        + Loc.Format($" · {char.ToUpperInvariant(AppVersion.Current.Channel[0])}{AppVersion.Current.Channel[1..]} channel");

    // "Download source code" (docs/patreon-and-updates.md §4.5 item 7): stable opens the public repo's tag; alpha and beta
    // fetch the source zip served next to the package (with the access token) into Downloads; none on internal/dev builds.
    public bool ShowsSource { get; } = UpdateFeeds.Source(AppVersion.Current) != null && (AppVersion.Current.Channel == UpdateChannels.Stable || App.Account.SignedIn);
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
        (SourceIdle, SourceStatus) = (false, Loc.Text("Downloading…"));
        Changed();
        try
        {
            using var http = new HttpClient(RouteFailover.Default, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new("Bearer", await App.Account.GetAccessTokenAsync() ?? throw new InvalidOperationException("sign in with Patreon first"));
            using var r = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            r.EnsureSuccessStatusCode();
            var file = Path.Combine(Windows.Storage.UserDataPaths.GetDefault().Downloads, $"SCSKiller-source-{v}.zip");
            await using (var f = File.Create(file + ".partial")) await r.Content.CopyToAsync(f);
            File.Move(file + ".partial", file, overwrite: true);
            SourceStatus = Loc.Text("Saved to ") + file;
        }
        catch (Exception e) { SourceStatus = Loc.Text("Couldn't download it: ") + e.Message; }
        SourceIdle = true;
        Changed();
    }

    // "Check for updates": every installed build (the design data shows it too); the state of the update next to it.
    public bool ShowsCheck { get; } = Updater.Installed || App.Core is Design.FakeScsKiller;
    public bool CanCheck => !Updater.Checking && !Updater.Restarting;
    public string UpdateNote => Updater.Downloading is { } d ? Loc.Format($"Downloading SCSKiller {d}…")
        : Updater.Checking ? Loc.Text("Checking for updates…")
        : Updater.Ready is { } v ? AutoInstall.ReadyNote(v, App.Core.Settings)
        : Updater.UpToDate ? Loc.Text("SCSKiller is up to date.") : "";
    public string? UpdateProblem => Updater.Problem;
    public bool HasUpdateProblem => Updater.Problem != null;

    public bool? InstallUpdatesAutomatically
    {
        get => App.Core.Settings.InstallUpdatesAutomatically;
        set
        {
            if (value is not { } v || v == App.Core.Settings.InstallUpdatesAutomatically) return;
            App.Core.Settings = App.Core.Settings with { InstallUpdatesAutomatically = v };
            Changed();   // the note says when it installs
        }
    }

    readonly Coalesced changed;
    /// <summary>While the page is shown.</summary>
    public void Watch(bool on)
    {
        Updater.Changed -= changed.Request;
        if (!on) return;
        Updater.Changed += changed.Request;
        Changed();
    }

    public List<Notice> Components { get; }
    public string Trademarks { get; }

    public AboutVm()
    {
        changed = new(App.Main.DispatcherQueue, Changed);
        using var s = typeof(AboutVm).Assembly.GetManifestResourceStream("THIRD-PARTY-NOTICES.md")!;
        (Components, Trademarks) = Parse(new StreamReader(s).ReadToEnd());
    }

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
            .Select(c => new Notice(c[0], c[1], c[2], c[3], c[4], c[5].StartsWith('[') ? texts.GetValueOrDefault(c[5][1..c[5].IndexOf(']')], "") : "",
                notes.GetValueOrDefault(c[0], "")))
            .ToList();
        var marks = string.Join('\n', Section("Trademarks").Split('\n').Where(l => l.StartsWith("- ")).Select(l => l[2..]));
        return (rows, marks);
    }
}
