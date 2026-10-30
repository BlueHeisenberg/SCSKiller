namespace SCSKiller.Ui.Platform;

static partial class Notifications
{
    /// <summary>Registered: toasts show and their clicks reach the app.</summary>
    public static bool Available { get; private set; }
    /// <summary>Why notifications couldn't register.</summary>
    public static string? Error { get; private set; }
}
