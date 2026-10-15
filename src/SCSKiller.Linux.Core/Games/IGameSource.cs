using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core.Games;

public interface IGameSource
{
    GameSource SourceType { get; }
    Task<IReadOnlyList<GameInfo>> DiscoverGamesAsync(CancellationToken cancellationToken = default);
}
