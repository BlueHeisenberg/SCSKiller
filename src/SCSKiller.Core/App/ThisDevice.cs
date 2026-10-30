namespace SCSKiller.Core.App;

public static partial class ThisDevice
{
    /// <summary>The device list's name for this PC: "Windows PC", "Linux PC" or "Steam Deck", never its own name.</summary>
    public static partial string Kind { get; }
}
