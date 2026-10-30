using SCSKiller.Core.App;

namespace SCSKiller.Ui;

public partial class App
{
    /// <summary>Off the UI thread, a release build only: Debug builds leave the menu as it is.</summary>
    static partial void RefreshMenuEntry(ScsKiller core)
    {
#if !DEBUG
        _ = Task.Run(() =>
        {
            if (MenuEntry.RefreshThis() is { } line) core.Log?.Report(line);
        });
#endif
    }
}
