using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Ui.Models;

/// <summary>Settings' Account card: a view over <see cref="Host.Account"/>, which changes on worker threads.</summary>
public sealed class AccountVm : Bindable
{
    static Account A => Host.Account!;
    readonly Coalesced changed;
    public AccountVm() => changed = new(Host.Ui, Changed);

    // The app sets Updater.Host once it runs the updater: until then, nothing reads the state that needs it.
    static bool Wired => Updater.Host.Core != null;

    /// <summary>While the page is shown. Signed in with nothing read yet this session: reads the status.</summary>
    public void Watch(bool on)
    {
        A.Changed -= changed.Request;
        Updater.Changed -= changed.Request;
        if (!on) return;
        A.Changed += changed.Request;
        Updater.Changed += changed.Request;
        Changed();
        if (A.SignedIn && A.Status == null && !A.Busy) _ = A.RefreshAsync();
    }

    // Failures show in the card (Account.Problem), so these never throw.
    public static void SignIn() => _ = A.SignInAsync();
    public static void CancelSignIn() => A.CancelSignIn();
    public static void RefreshStatus() => _ = A.RefreshAsync();
    public static void SignOut() { if (!Updater.Restarting) _ = A.SignOutAsync(); }

    /// <summary>"Go back to stable now": the latest stable, even though it's older than this pre-release (a downgrade, allowed this once).</summary>
    public static void BackToStable()
    {
        if (Updater.Restarting) return;   // the restart applies the update it waited for, or none
        Host.Core.Settings = Host.Core.Settings with { UpdateChannel = UpdateChannels.Stable };
        if (Wired) _ = Updater.CheckAsync(backToStable: true);
    }

    /// <summary>The post-sign-in share prompt (<see cref="OffersSharing"/>): asked once, ever, either answer dismisses it.</summary>
    public static void AnswerSharing(bool share) =>
        Host.Core.Settings = Host.Core.Settings with { SharePromptDismissed = true, ShareRecordings = share || Host.Core.Settings.ShareRecordings };

    public bool SignedIn => A.SignedIn;
    public bool SignedOut => !A.SignedIn;
    public bool SigningIn => A.SigningIn;
    public bool Busy => A.Busy;
    public bool Idle => !A.Busy;
    public string? Problem => A.Problem;
    public bool HasProblem => A.Problem != null;
    public bool OffersSharing => A.OffersSharing(Host.Core.Settings);
    public void Refresh() => Changed();   // the settings changed (OffersSharing)

    // "Download shared shader hashes": enabled only with "db" (follows sign-in, sign-out, refresh and a lapse through
    // Changed); the setting keeps its value. One quiet line under it: why it's off, or its last failure.
    public bool HasDb => A.HasDb;
    // Unticked without "db" whatever the setting says; the binding writes that false back, which must not reach the setting.
    public bool? UseCommunityDb
    {
        get => A.HasDb && Host.Core.Settings.UseCommunityDb;
        set
        {
            if (!A.HasDb || value is not { } v || v == Host.Core.Settings.UseCommunityDb) return;
            Host.Core.Settings = Host.Core.Settings with { UseCommunityDb = v };
            Changed();
        }
    }
    public string? DbNote => !A.SignedIn ? "The list of games it covers is checked for everyone. The shared hashes need a Patreon subscription."
        : A.Status is not { } st ? null   // not read yet: the status line above says "Checking…"
        : !A.HasDb ? "Your Patreon membership doesn't include it right now. What was downloaded before keeps working."
        : (Host.Core as ScsKiller)?.Community?.Problem;
    public bool HasDbNote => DbNote != null;

    public string Benefits => A.Status switch
    {
        null => A.Busy ? "Checking your supporter status…" : "Supporter status not checked yet.",
        { Benefits: { } b } => b,   // the flag names live in Core (AccountStatus) so they're testable
        _ => "No supporter benefits right now. They start once your Patreon membership is active: then click Refresh status.",
    };

    // Update channel: a picker only when the token grants more than stable.
    static readonly Dictionary<string, string> ChannelNames = new()
    {
        [UpdateChannels.Stable] = "Stable", [UpdateChannels.Beta] = "Beta (Patreon supporter)",
        [UpdateChannels.Alpha] = "Alpha (Patreon backer)", [UpdateChannels.Internal] = "Internal",
    };
    static IReadOnlyList<string> Offered => UpdateChannels.Offered(A.Status?.Ent);
    static string Chosen => UpdateChannels.Effective(Host.Core.Settings.UpdateChannel, AppVersion.Current.Channel, A.Status?.Ent);
    List<string> labels = [];
    public List<string> ChannelLabels => labels.SequenceEqual(Offered.Select(c => ChannelNames[c])) ? labels : labels = Offered.Select(c => ChannelNames[c]).ToList();   // same list: the ComboBox keeps its selection
    // a Flatpak or a package: no channel to pick, and the note says where its updates come from
    static string? UpdatesFrom => Wired ? Updater.Installation.UpdatesFrom : null;
    bool Entitled => A.SignedIn && Offered.Count > 1;
    public bool ShowsChannels => Entitled && UpdatesFrom == null;
    public bool CanPickChannel => !Updater.Restarting;
    public bool CanSignOut => Idle && !Updater.Restarting;   // signing out can change the channel a restart applies
    public int ChannelIndex
    {
        get => Math.Max(0, Offered.ToList().IndexOf(Chosen));
        set
        {
            if (value < 0 || value >= Offered.Count || Offered[value] == Chosen || Updater.Restarting) return;   // -1: the list was replaced
            Host.Core.Settings = Host.Core.Settings with { UpdateChannel = Offered[value] };
            if (Wired) _ = Updater.CheckAsync();
            Changed();
        }
    }
    /// <summary>"Go back to stable now": on an installed pre-release build, whatever the account.</summary>
    public bool OffersBackToStable => Wired && Updater.Installed && AppVersion.Current.Channel != UpdateChannels.Stable;
    public bool ShowsUpdates => Entitled || OffersBackToStable || UpdatesFrom != null;
    public string ChannelNote => UpdatesFrom ?? (Updater.Checking ? "Checking for updates…"
        : Wired && Updater.Ready is { } v ? AutoInstall.ReadyNote(v, Updater.InstallsAutomatically)
        : (InstallsAutomatically ? "Updates download in the background and install the next time SCSKiller starts or quits, never during a compile or a game."
            : "Updates download in the background, and Restart to update installs them.") + " Leaving an early channel keeps this build until Stable passes it.");
    // unwired, nothing is downloaded: the next download's channel is the chosen one, as Updater.Channel says then
    static bool InstallsAutomatically => Wired ? Updater.InstallsAutomatically : AutoInstall.On(Host.Core.Settings, Chosen);
    public string? UpdateProblem => Updater.Problem;
    public bool HasUpdateProblem => Updater.Problem != null;
}
