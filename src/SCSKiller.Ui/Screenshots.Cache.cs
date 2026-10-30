using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Pages;

namespace SCSKiller.Ui;

static partial class Screenshots
{
    // WinUI takes "cache" with the pages' round and the rest after settings-new-shaders: here all at once, the same time in all
    static async Task CachePages(MainWindow main, Func<string, int, string, Task> shot, Func<ContentDialog, string, Task> dialog)
    {
        main.Navigate(typeof(CachePage));
        await shot("cache", 1000, "");
        main.Navigate(typeof(CachePage));
        await Task.Delay(1000);
        var cache = (CachePage)main.Page!;
        cache.ScrollToEnd();   // the lower groups
        await shot("cache-end", 400, "");
        await dialog(CachePage.ClearEverythingDialog(cache.Vm.Inventory!).Dialog, "cache-clear-everything");
        await cache.Vm.DeleteAsync(cache.Vm.Inventory!.Everything(withOthers: false));   // the sample deletes nothing
        cache.ScrollToTop();
        await shot("cache-deleted", 400, "");
    }
}
