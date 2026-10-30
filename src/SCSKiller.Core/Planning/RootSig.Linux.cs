namespace SCSKiller.Core.Planning;

public static unsafe partial class RootSig
{
    internal static partial byte[] Call(byte* desc) => SerializeManaged(desc);
}
