using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SCSKiller.Core;
using System.Globalization;
using SCSKiller.Core.App;

namespace SCSKiller.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsVm Vm { get; } = new();
    public AccountVm Patreon { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();
        PageTitle.Text = I18n.T("SettingsTitle");
        TxtCurrentLimit.Text = I18n.T("SettingsCurrentLimit");
        TxtUsedNow.Text = I18n.T("SettingsUsedNow");
        NewLimitLabel.Text = I18n.T("SettingsNewLimit");
        TxtApplyBtn.Text = I18n.T("SettingsApplyAdmin");
        TxtSwitchBackNote.Text = I18n.T("SettingsSwitchBackNote");
        TxtAfterDriverTitle.Text = I18n.T("SettingsAfterDriverTitle");
        TxtAfterDriverDesc.Text = I18n.T("SettingsAfterDriverDesc");
        RadioAskMeTitle.Text = I18n.T("SettingsAskMe");
        RadioAskMeSub.Text = I18n.T("SettingsAskMeSub");
        RadioAutoIdleTitle.Text = I18n.T("SettingsAutoIdle");
        RadioAutoIdleSub.Text = I18n.T("SettingsAutoIdleSub");
        RadioOffTitle.Text = I18n.T("SettingsModeOff");
        RadioOffSub.Text = I18n.T("SettingsModeOffSub");
        ChkPauseGaming.Content = I18n.T("SettingsPauseWhileGaming");
        TxtBgThreads.Text = I18n.T("SettingsBackgroundThreads");
        AutoBox.Content = I18n.T("SettingsNvidiaAutoTitle");
        ComboSysUtil.Header = I18n.T("SettingsNvidiaSysUtil");
        TxtAutoApplyBtn.Text = I18n.T("SettingsApplyAdmin");
        InfoNvidiaRec.Title = I18n.T("SettingsNvidiaRecTitle");
        InfoNvidiaRec.Message = I18n.T("SettingsNvidiaRecMsg");
        TxtPatreonTitle.Text = I18n.T("SettingsPatreonTitle");
        TxtPatreonDesc.Text = I18n.T("SettingsPatreonDesc");
        BtnSignInPatreon.Content = I18n.T("SettingsPatreonSignIn");
        BtnCancelSignIn.Content = I18n.T("SettingsCancel");
        TxtBrowserPrompt.Text = I18n.T("SettingsBrowserPrompt");
        TxtSignedInTitle.Text = I18n.T("SettingsSignedInTitle");
        BtnRefreshAccount.Content = I18n.T("SettingsRefreshStatus");
        BtnSignOut.Content = I18n.T("SettingsSignOut");
        InfoPatreonSharing.Title = I18n.T("SettingsSharingTitle");
        InfoPatreonSharing.Message = I18n.T("SettingsSharingMsg");
        BtnShareAnon.Content = I18n.T("SettingsShareAnon");
        BtnNotNow.Content = I18n.T("SettingsNotNow");
        ComboUpdateChannel.Header = I18n.T("SettingsUpdateChannel");
        BtnBackToStable.Content = I18n.T("SettingsBackToStable");
        TxtDownloadHashes.Text = I18n.T("SettingsDownloadHashes");
        TxtDownloadHashesSub.Text = I18n.T("SettingsDownloadHashesSub");
        TxtShareHashes.Text = I18n.T("SettingsShareHashes");
        TxtShareHashesSub.Text = I18n.T("SettingsShareHashesSub");
        TxtQueueSpeedTitle.Text = I18n.T("SettingsQueueSpeedTitle");
        TxtThreadsLabel.Text = I18n.T("SettingsCompileThreads");
        TxtMaxMemLabel.Text = I18n.T("SettingsMaxMemory");
        TxtMaxModeTitle.Text = I18n.T("SettingsMaximumMode");
        TxtMaxModeSub.Text = I18n.T("SettingsMaximumModeSub");
        TxtBgSectionTitle.Text = I18n.T("SettingsBackgroundTitle");
        TxtStartWinTitle.Text = I18n.T("SettingsStartWindows");
        TxtStartWinSub.Text = I18n.T("SettingsStartWindowsSub");
        TxtCloseQuitsTitle.Text = I18n.T("SettingsCloseQuits");
        TxtCloseQuitsSub.Text = I18n.T("SettingsCloseQuitsSub");
        TxtScanStartTitle.Text = I18n.T("SettingsScanAtStart");
        TxtScanStartSub.Text = I18n.T("SettingsScanAtStartSub");
        TxtNotifyShadersTitle.Text = I18n.T("SettingsNotifyShaders");
        TxtNotifyShadersSub.Text = I18n.T("SettingsNotifyShadersSub");
        TxtDailyCheckTitle.Text = I18n.T("SettingsDailyCheck");
        TxtDailyCheckSub.Text = I18n.T("SettingsDailyCheckSub");
        TxtRecordAllTitle.Text = I18n.T("SettingsRecordAllTitle");
        TxtRecordAllDesc.Text = I18n.T("SettingsRecordAllDesc");
        RecordingLimitLabel.Text = I18n.T("SettingsRecordLimit");
        TxtRecordLimitDesc.Text = I18n.T("SettingsRecordLimitDesc");
        ExpanderSpaceByGame.Header = I18n.T("SettingsSpaceByGame");
        var refresh = new Coalesced(DispatcherQueue, Vm.GamesChanged);   // reconcile changes the recorder count
        void OnChanged(GameState _) => refresh.Request();
        Loaded += (_, _) => { Patreon.Watch(true); App.Core.GameChanged += OnChanged; };
        Unloaded += (_, _) => { Patreon.Watch(false); App.Core.GameChanged -= OnChanged; };
        Vm.PropertyChanged += (_, _) => Patreon.Refresh();   // e.g. the share checkbox ticked: the prompt goes
    }

    // Account: failures show in the card (Account.Problem), so these never throw.
    void OnSignIn(object _, RoutedEventArgs __) => _ = App.Account.SignInAsync();
    void OnCancelSignIn(object _, RoutedEventArgs __) => App.Account.CancelSignIn();
    void OnRefreshAccount(object _, RoutedEventArgs __) => _ = App.Account.RefreshAsync();
    void OnSignOut(object _, RoutedEventArgs __) { if (!Updater.Restarting) _ = App.Account.SignOutAsync(); }

    // The latest stable, even though it's older than this pre-release (a downgrade, allowed this once).
    void OnBackToStable(object _, RoutedEventArgs __)
    {
        if (Updater.Restarting) return;   // the restart applies the update it waited for, or none
        App.Core.Settings = App.Core.Settings with { UpdateChannel = UpdateChannels.Stable };
        _ = Updater.CheckAsync(backToStable: true);
    }

    // The post-sign-in share prompt (Account.OffersSharing).
    void OnShare(object _, RoutedEventArgs __) => Answer(share: true);
    void OnNotNow(object _, RoutedEventArgs __) => Answer(share: false);
    void Answer(bool share)   // asked once, ever: either answer dismisses it
    {
        App.Core.Settings = App.Core.Settings with { SharePromptDismissed = true, ShareRecordings = share || App.Core.Settings.ShareRecordings };
        Vm.Refresh();   // the share checkbox; its PropertyChanged refreshes the prompt too
    }

    public void ScrollToEnd() => Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true);
    /// <summary>--screenshots: the Patreon card at the top.</summary>
    public void ScrollToPatreon() => PatreonCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, VerticalOffset = -16, AnimationDesired = false });

    // SetCacheLimit is a global driver setting that needs admin: only after the user's explicit OK.
    async void OnApply(object _, RoutedEventArgs __)
    {
        var (label, limit) = SettingsVm.Sizes[SizeBox.SelectedIndex];
        if (!await App.ConfirmAsync(this, $"Set the shader cache limit to {label}?",
                $"This is a global {Fmt.Vendor(App.Core.Vendor.Vendor)} driver setting: it applies to every game on this PC, not only the ones SCSKiller compiles, " +
                "and stays until you change it. Windows will ask for administrator permission.\n\n" +
                "Raising it deletes nothing. Lowering it below what is used now makes the driver delete older entries, and those games stutter again. " +
                "You can switch back to the driver default here at any time.",
                "Change limit")) return;

        ApplyButton.IsEnabled = false;
        var size = limit.IsDriverDefault ? "default" : limit.Bytes is { } b ? (b / (double)(1L << 30)).ToString(CultureInfo.InvariantCulture) : "unlimited";
        (Vm.ApplyMessage, Vm.ApplySeverity) = await AsAdminAsync(() => App.Core.Vendor.SetCacheLimit(limit), ["cache", "set", size]) switch
        {
            null => ("Cancelled.", InfoBarSeverity.Informational),
            { Ok: true } => ($"Shader cache limit set to {label}.", InfoBarSeverity.Success),
            var r => ("The limit was not changed: " + r.Message, InfoBarSeverity.Error),
        };
        ApplyButton.IsEnabled = true;
        Vm.Refresh();
    }

    // Admin-only actions: in this process when it is already elevated (or shows the fake), else the bundled CLI behind a
    // UAC prompt (Elevated.Run). Null = the user cancelled the prompt; an exception is a failure like the CLI's.
    static Task<Elevated.Result?> AsAdminAsync(Action inProcess, string[] cliArgs) => Task.Run(() =>
    {
        try
        {
            if (App.Core is not Design.FakeScsKiller && !Elevated.IsAdmin) return Elevated.Run(cliArgs);
            inProcess();
            return new Elevated.Result(true, "");
        }
        catch (Exception ex) { return new Elevated.Result(false, ex.Message); }
    });

    // Auto Shader Compilation: a global driver setting + the driver's idle task, admin: only after the user's explicit OK.
    async void OnApplyAuto(object _, RoutedEventArgs __)
    {
        var level = Vm.AutoChoice;
        if (!await App.ConfirmAsync(this, level == AutoShaderCompilation.Off ? "Turn off NVIDIA Auto Shader Compilation?" : $"Turn on NVIDIA Auto Shader Compilation ({level})?",
                "Auto Shader Compilation is a beta feature by NVIDIA®.", level == AutoShaderCompilation.Off ? "Turn off" : "Turn on")) return;

        AutoApplyButton.IsEnabled = false;
        // --for-user: the elevated CLI refuses when UAC elevated another administrator (NvOSC would register the task for them).
        string[] cli = ["nvidia-auto-shader", level.ToString().ToLowerInvariant(), Elevated.ForUserArg, Elevated.CurrentUserSid];
        (Vm.AutoMessage, Vm.AutoSeverity) = await AsAdminAsync(() => App.Core.Vendor.SetAutoShaderCompilation(level), cli) switch
        {
            null => ("Cancelled.", InfoBarSeverity.Informational),
            { Ok: true } => (level == AutoShaderCompilation.Off ? "Turned off." : $"Turned on ({level}).", InfoBarSeverity.Success),
            var r => ("Not changed: " + r.Message, InfoBarSeverity.Error),
        };
        AutoApplyButton.IsEnabled = true;
        Vm.Refresh();   // re-reads the setting and the task
    }
}
