using MemoryPack;

namespace SilverModManager.ProxyConsole.Messages;

/// <summary>
/// Sent by the main process when the verb a client asked for has finished, so the client can exit with the verb's code.
/// </summary>
[MemoryPackable]
public partial class CommandExited : IMessage
{
    public required int ExitCode { get; init; }
}
