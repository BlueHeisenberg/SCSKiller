namespace SCSKiller.Core.App;

/// <summary>The named mutexes SCSKiller's processes share (the app, the CLI, the scheduled task and the helpers), by their
/// Windows names. Windows keeps them as they are, Local\ (the logon session) or Global\. On Linux each is the user's own
/// (in the folder the running .NET keeps them in, 0700, under folders no other user can replace) and seen in all of the
/// user's sessions that share that folder, and nothing is opened while it can't be found or trusted (<see cref="Unsafe"/>).</summary>
static partial class NamedMutex
{
    /// <summary><paramref name="name"/>, made when no process has it open, not owned. IOException while
    /// <see cref="Unsafe"/>.</summary>
    internal static partial Mutex Open(string name);

    /// <summary>Some process has <paramref name="name"/> open. On Linux also while <see cref="Unsafe"/> or the name can't be
    /// read: a check that can't tell counts as held, so nothing goes ahead on it.</summary>
    internal static partial bool Held(string name);

    /// <summary>Why the names can't be opened safely: null when they can, always on Windows. <paramref name="root"/>: the
    /// folder checked, tests only.</summary>
    internal static partial string? Unsafe(string? root = null);
}
