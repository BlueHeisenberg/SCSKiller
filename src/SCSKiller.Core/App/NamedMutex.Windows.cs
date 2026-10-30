namespace SCSKiller.Core.App;

static partial class NamedMutex
{
    internal static partial Mutex Open(string name) => new(false, name);

    internal static partial bool Held(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var m)) return false;
        m.Dispose();
        return true;
    }

    internal static partial string? Unsafe(string? root) => null;
}
