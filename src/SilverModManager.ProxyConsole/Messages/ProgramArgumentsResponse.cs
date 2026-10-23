using MemoryPack;

namespace SilverModManager.ProxyConsole.Messages;

[MemoryPackable]
public partial class ProgramArgumentsResponse : IMessage
{
    public required string[] Arguments { get; init; }

}
