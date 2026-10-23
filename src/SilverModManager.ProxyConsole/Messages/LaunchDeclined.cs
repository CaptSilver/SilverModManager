using MemoryPack;

namespace SilverModManager.ProxyConsole.Messages;

/// <summary>
/// The main process will not run the launch; nothing was started or changed.
/// </summary>
[MemoryPackable]
public partial class LaunchDeclined : IMessage
{
    public required string Reason { get; init; }
}
