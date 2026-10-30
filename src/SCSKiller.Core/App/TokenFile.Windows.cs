namespace SCSKiller.Core.App;

public sealed partial class TokenFile<T>
{
    /// <summary>Null when there is none, or it is another user's or damaged. Nothing is refused here.</summary>
    public Kept? Peek(out string? refused)
    {
        refused = null;
        return Dpapi.Load<T>(path) is { } value ? new(value, null) : null;
    }

    public Task<T?> ReadAsync(Kept kept, CancellationToken ct) => Task.FromResult(kept.Value);

    /// <summary>Nothing to do: no keyring here.</summary>
    public Task SweepAsync() => Task.CompletedTask;

    /// <summary>Nothing is written once <paramref name="ct"/> is cancelled. <paramref name="commit"/> runs right after the
    /// write, before another save or delete. <c>FellBack</c>: never, there is no keyring here.</summary>
    public async Task<(Kept Kept, bool FellBack)> SaveAsync(T value, CancellationToken ct = default, Action<Kept, bool>? commit = null)
    {
        await gate.WaitAsync(ct);   // throws once cancelled, however long another save took
        try
        {
            Dpapi.Save(path, value);
            var kept = new Kept(value, null);
            commit?.Invoke(kept, false);
            return (kept, false);
        }
        finally { gate.Release(); }
    }

    /// <summary>The file, unless it holds another token than <paramref name="kept"/> (saved since). Null: whatever it holds.</summary>
    public async Task DeleteAsync(Kept? kept)
    {
        await gate.WaitAsync();
        try
        {
            if (kept == null || Peek(out _) is not { } now || Same(now, kept)) File.Delete(path);
        }
        finally { gate.Release(); }
    }
}
