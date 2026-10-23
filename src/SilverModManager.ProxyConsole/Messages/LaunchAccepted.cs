using MemoryPack;

namespace SilverModManager.ProxyConsole.Messages;

/// <summary>
/// The main process has taken the launch and will answer with <see cref="LaunchExited"/>.
/// </summary>
[MemoryPackable]
public partial class LaunchAccepted : IMessage
{
}
