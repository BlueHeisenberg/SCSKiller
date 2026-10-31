using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Ui.Models;

/// <summary>What the view models run on, set by the UI before it builds a page.</summary>
public static class Host
{
    public static IScsKiller Core { get; set; } = null!;
    /// <summary>app.log: the data folder's, or the design data's temporary folder's.</summary>
    public static IProgress<string>? Log { get; set; }
    /// <summary>The data folder's store: the real one, or the design data's temporary folder.</summary>
    public static AppStore Store { get; set; } = null!;
    public static IUiThread Ui { get; set; } = null!;
    /// <summary>Sign in with Patreon; null reads as signed out.</summary>
    public static Account? Account { get; set; }
    public static IGameIcons Icons { get; set; } = new NoIcons();
    /// <summary>Set when <see cref="Core"/> is the design data (--fake, --screenshots).</summary>
    public static IDesignData? Design { get; set; }
    /// <summary>What this OS's app offers, in a design run too.</summary>
    public static AppPlatform Platform { get; set; } = AppPlatform.Windows;
    /// <summary>Whether the notification-area icon shows: on Linux as the tray host says, on Windows always.</summary>
    public static TrayState Tray { get; set; } = new();
    /// <summary>Settings' theme picked: the window and its dialogs follow it.</summary>
    public static Action<AppTheme?> ApplyTheme { get; set; } = _ => { };
    /// <summary>Settings' Start with Windows changed by the user: the sign-in entry follows it. Done when the entry is as
    /// it will be (a Flatpak's portal answers later).</summary>
    public static Func<Task> StartWithWindowsChanged { get; set; } = () => Task.CompletedTask;
    /// <summary>Whether this copy starts at sign-in, read back from the entry (Linux) for Settings' checkbox; null where the
    /// setting is all there is.</summary>
    public static Func<bool?> StartsAtSignIn { get; set; } = () => null;
    /// <summary>Settings' new-shaders choice changed: the games it now compiles or no longer compiles are queued or withdrawn.</summary>
    public static Action NewShadersChanged { get; set; } = () => { };
    public static Func<string, Task> CopyText { get; set; } = _ => Task.CompletedTask;

    sealed class NoIcons : IGameIcons
    {
        public object? Get(string exePath) => null;
        public event Action? Changed { add { } remove { } }
    }
}

/// <summary>Whether a tray host shows the notification-area icon. UI thread.</summary>
public sealed class TrayState
{
    bool shown = true;

    public bool Shown
    {
        get => shown;
        set
        {
            if (value == shown) return;
            shown = value;
            Changed?.Invoke();
        }
    }

    public event Action? Changed;
}

public interface IUiThread
{
    /// <summary>Queues <paramref name="run"/> on the UI thread; false when the UI is shutting down.</summary>
    bool TryEnqueue(Action run);
}

/// <summary>Game icons, as the UI's image type. UI thread only.</summary>
public interface IGameIcons
{
    /// <summary>The icon, or null while it loads, when the exe isn't there or has none (the row shows its tile).</summary>
    object? Get(string exePath);
    /// <summary>An icon loaded or failed after its row was built: rebuild the rows.</summary>
    event Action? Changed;
}

/// <summary>The design data's members that IScsKiller doesn't carry (the real core's are on <see cref="ScsKiller"/>).</summary>
public interface IDesignData
{
    ManualSource Manual { get; }
    string? ScanProgress { get; }
    string? AfterUpdateLine { get; }
    string? FoldLine(string gameId);
    string? FoldingLine { get; }
    IReadOnlyList<string> FoldingGames { get; }
    void RenameManualGame(string gameId, string name);
}
