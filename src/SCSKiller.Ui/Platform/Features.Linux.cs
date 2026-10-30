using SCSKiller.Core.Games;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Platform;

static class Features
{
    public static AppPlatform Current { get; } = OperatingSystem.IsMacOS() ? AppPlatform.MacOS : AppPlatform.Linux(SteamSource.Installed);
}
