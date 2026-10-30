namespace SCSKiller.Core.App;

/// <summary>The apps' watch over the before and after pairs, fed each changed game on the UI thread: a sound once a run's
/// play time is reached, the report once a pair turns Done.</summary>
public sealed class BeforeAfterWatch
{
    // the stage last seen per game, and whether its run's sound played
    readonly Dictionary<string, (BeforeAfterStage? Stage, bool Sounded)> seen = [];

    public BeforeAfterWatch(IEnumerable<GameState> games)
    {
        foreach (var g in games) seen[g.Game.Id] = (g.BeforeAfter?.Stage, false);
    }

    /// <summary><c>Sound</c>: the playing run just reached its play time. <c>Report</c>: the pair just turned Done, not one
    /// that was Done already or that the watch never saw.</summary>
    public (bool Sound, bool Report) Changed(GameState g)
    {
        var (id, now) = (g.Game.Id, g.BeforeAfter);
        bool had = seen.TryGetValue(id, out var was);
        bool sounded = had && was.Stage == now?.Stage && was.Sounded, sound = false;
        if (now is { Stage: BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying } p && !sounded && p.PlayedSeconds >= p.Minutes * 60)
            sound = sounded = true;
        seen[id] = (now?.Stage, sounded);
        return (sound, had && was.Stage is not (null or BeforeAfterStage.Done) && now?.Stage == BeforeAfterStage.Done);
    }
}
