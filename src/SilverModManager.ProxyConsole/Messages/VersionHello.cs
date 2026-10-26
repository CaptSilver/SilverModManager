using MemoryPack;

namespace SilverModManager.ProxyConsole.Messages;

/// <summary>
/// The first thing the main process sends on a new connection: which build it is. The client compares it with its own
/// before sending anything, because two builds do not necessarily speak the same messages.
/// </summary>
[MemoryPackable]
public partial class VersionHello : IMessage
{
    public required string Version { get; init; }

    public string? CommitHash { get; init; }
}
