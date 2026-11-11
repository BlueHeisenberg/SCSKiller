using System.Runtime.InteropServices;

namespace SCSKiller.Core.App;

public static partial class UpdateChannels
{
    public static partial FeedOs Os => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? FeedOs.LinuxArm64 : FeedOs.Linux;
}
