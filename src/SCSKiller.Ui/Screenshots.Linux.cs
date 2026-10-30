using Avalonia.Styling;
using SCSKiller.App.Design;
using SCSKiller.Core;
using SCSKiller.Ui.Models;
using SCSKiller.Ui.Pages;

namespace SCSKiller.Ui;

static partial class Screenshots
{
    // A scan that found no games: the native Steam's are the ones listed, then no native Steam
    private static async partial Task PlatformShots(MainWindow main, FakeScsKiller fake, IGpuVendorBackend sampleGpu, Func<string, Task> shot)
    {
        var (platform, vendor) = (Host.Platform, fake.Vendor);
        fake.Vendor = sampleGpu;
        main.ShowGpu();
        fake.HideGames = true;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            main.RequestedThemeVariant = theme;
            foreach (var (name, steam) in new[] { ("library-empty", true), ("library-no-steam", false) })
            {
                Host.Platform = AppPlatform.Linux(() => steam);
                main.Navigate(typeof(LibraryPage));
                ((LibraryPage)main.Page!).Vm.Refresh();
                await shot(name);
            }
        }
        fake.ShowGames();
        (Host.Platform, fake.Vendor) = (platform, vendor);
        main.ShowGpu();
    }
}
