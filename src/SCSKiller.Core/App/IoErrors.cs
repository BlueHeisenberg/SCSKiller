namespace SCSKiller.Core.App;

static partial class IoErrors
{
    /// <summary>Another holder has the file open without sharing it.</summary>
    internal static partial bool HeldElsewhere(IOException e);

    /// <summary>The file is held by another program or access to it is refused: a later read may succeed.</summary>
    internal static bool Busy(Exception e) => e is UnauthorizedAccessException || e is IOException io && HeldElsewhere(io);
}
