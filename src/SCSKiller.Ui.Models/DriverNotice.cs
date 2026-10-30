using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Ui.Models;

/// <summary>The driver-update notification that asks what to do, once per driver and set of stale games. Only where the
/// OS has the driver-update setting (<see cref="AppPlatform.DriverUpdateModes"/>).</summary>
public sealed class DriverNotice(Action<IReadOnlyList<GameState>> show)
{
    string? told;   // the driver and games the last one was about

    /// <summary>False when there is nothing to notify.</summary>
    public bool Show(IScsKiller core)
    {
        if (!Host.Platform.DriverUpdateModes || !core.ShouldNotifyStale()) return false;
        var stale = core.DriverStaleGames();
        var about = $"{(core as ScsKiller)?.DriverId}|{string.Join('|', stale.Select(g => g.Game.Id).Order())}";
        if (about != told) show(stale);
        told = about;
        return true;
    }
}
