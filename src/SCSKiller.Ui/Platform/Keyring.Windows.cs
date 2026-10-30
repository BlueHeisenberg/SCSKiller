using SCSKiller.Core.App;

namespace SCSKiller.Ui.Platform;

static partial class Keyring
{
    /// <summary>None: the token files use DPAPI on Windows.</summary>
    public static IKeyring? System() => null;
}
