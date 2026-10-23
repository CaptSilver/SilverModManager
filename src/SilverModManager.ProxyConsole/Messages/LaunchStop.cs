using MemoryPack;

namespace SilverModManager.ProxyConsole.Messages;

/// <summary>
/// Sent by the client of an accepted launch when it was asked to stop, such as by Steam's Stop button.
/// </summary>
[MemoryPackable]
public partial class LaunchStop : IMessage
{
}
