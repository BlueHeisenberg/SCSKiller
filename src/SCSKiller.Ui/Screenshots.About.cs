using SCSKiller.App.Design;
using SCSKiller.Core.App;
using SCSKiller.Ui.Models;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Pages;

namespace SCSKiller.Ui;

static partial class Screenshots
{
    static async Task AboutPages(Func<ContentDialog, string, Task> dialog)
    {
        await dialog(WhatsNewDialog.Build(WhatsNew.Section(WhatsNew.Changelog, "1.2.4")!), "whatsnew");
        if (WhatsNew.ForAbout(AppVersion.Current) is { } current) await dialog(WhatsNewDialog.Build(current), "whatsnew-current");
        await dialog(StartDialogs.ActiveCheckDialog(), "daily-check");
    }

    // About: as it opens, then with the trademarks and the first licence open
    static async Task AboutShots(MainWindow main, Func<string, int, Task> shot)
    {
        main.Navigate(typeof(AboutPage));
        await shot("about", 1000);
        foreach (var (name, licence) in new[] { ("about-trademarks", false), ("about-licence", true) })
        {
            ((AboutPage)main.Page!).ShowExpanded(licence);
            await shot(name, 800);
        }
    }

    // About's channel line: signed out, a tier without alpha builds, an alpha tier, beta picked on it
    static async Task AboutChannelShots(MainWindow main, Func<string, int, string, Task> shot)
    {
        foreach (var (name, ent, pick) in new (string, string[]?, string?)[] { ("signedout", null, null), ("beta-tier", ["db", "beta"], null), ("alpha-tier", ["db", "beta", "alpha"], null), ("beta-pick", ["db", "beta", "alpha"], UpdateChannels.Beta) })
        {
            if (ent == null) await App.Account.SignOutAsync();
            else { FakeAccount.Ent = ent; await App.Account.SignInAsync(); }
            Host.Core.Settings = Host.Core.Settings with { UpdateChannel = pick };
            main.Navigate(typeof(LibraryPage));
            main.Navigate(typeof(AboutPage));
            await shot("about-channel-" + name, 1000, "");
        }
        Host.Core.Settings = Host.Core.Settings with { UpdateChannel = null };
        await App.Account.SignOutAsync();
    }
}
