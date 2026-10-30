using SCSKiller.Core.App;
using SCSKiller.App.Design;
using SCSKiller.Ui.Models;
using SCSKiller.Ui.Pages;

namespace SCSKiller.Ui;

static partial class Screenshots
{
    // Opening Settings must save nothing: a binding that writes a value back shows as a new settings record.
    static async Task SettingsPages(MainWindow main, Func<string, int, string, Task> shot)
    {
        var before = Host.Core.Settings;
        main.Navigate(typeof(SettingsPage));
        await shot("settings", 1000, "");
        if (!ReferenceEquals(before, Host.Core.Settings)) throw new InvalidOperationException("Opening Settings saved the settings");
        Shown(main).ScrollToEnd();   // the lower half: NVIDIA Auto Shader Compilation
        await shot("settings-end", 400, "");
        Shown(main).ScrollToNewShaders();
        await shot("settings-new-shaders", 400, "");
    }

    static SettingsPage Shown(MainWindow main) => (SettingsPage)main.Page!;

    // The Patreon card: signed out, signed in with "db" (the share prompt), signed out again (the download checkbox
    // turns off live, on the same page), signed in without "db".
    static async Task SettingsPatreonCard(MainWindow main, Func<string, int, string, Task> shot)
    {
        main.NavigateToPatreon();   // a game page's Patreon link
        await Task.Delay(1000);
        if (Shown(main).ScrolledBy == 0) throw new InvalidOperationException("Going to the Patreon card left Settings at the top");
        foreach (var (name, ent) in new (string, string[]?)[] { ("signedout", null), ("db", ["db", "beta"]), ("signedout-again", null), ("nodb", ["beta"]) })
        {
            if (ent == null) await App.Account.SignOutAsync();
            else { FakeAccount.Ent = ent; await App.Account.SignInAsync(); }
            await Task.Delay(1000);
            Shown(main).ScrollToPatreon();
            await shot("settings-account-" + name, 400, "");
            if (Host.Core.Settings.UpdateChannel != null) throw new InvalidOperationException($"Signing in or out chose the {Host.Core.Settings.UpdateChannel} channel");
        }
        Shown(main).Patreon.Refresh();   // a refresh that keeps the channel list doesn't hold back a pick right after it
        // picked as UI Automation picks: each saves. A stable build's picker already shows Stable, where picking it again
        // changes nothing, so Beta first
        if (Shown(main).Patreon.ChannelIndex == 0)
        {
            Shown(main).PickChannel(1);
            if (Host.Core.Settings.UpdateChannel != UpdateChannels.Beta) throw new InvalidOperationException("Picking Beta didn't save it");
            await Task.Delay(400);   // the picker shows the saved channel again on the next background pass
        }
        Shown(main).PickChannel(0);
        if (Host.Core.Settings.UpdateChannel != UpdateChannels.Stable) throw new InvalidOperationException("Picking Stable didn't save it");
        Host.Core.Settings = Host.Core.Settings with { UpdateChannel = null };
        await App.Account.SignOutAsync();
    }

    static async Task SettingsMin(MainWindow main, Func<string, int, string, Task> shot)
    {
        main.Navigate(typeof(SettingsPage));
        await shot("settings-min", 1000, "");
        Shown(main).ScrollToEnd();
        await shot("settings-min", 400, "-end");
    }
}
