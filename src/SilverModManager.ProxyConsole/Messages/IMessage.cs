using MemoryPack;

namespace SilverModManager.ProxyConsole.Messages;

/// <summary>
/// A message that can be sent between the client and the server.
/// </summary>
[MemoryPackable]
[MemoryPackUnion(0x1, typeof(ProgramArgumentsRequest))]
[MemoryPackUnion(0x2, typeof(ProgramArgumentsResponse))]
[MemoryPackUnion(0x3, typeof(Render))]
[MemoryPackUnion(0x4, typeof(Clear))]
[MemoryPackUnion(0x5, typeof(LaunchRequest))]
[MemoryPackUnion(0x6, typeof(LaunchDeclined))]
[MemoryPackUnion(0x7, typeof(LaunchAccepted))]
[MemoryPackUnion(0x8, typeof(LaunchExited))]
[MemoryPackUnion(0x9, typeof(LaunchStop))]
[MemoryPackUnion(0xA, typeof(CommandExited))]
[MemoryPackUnion(ushort.MaxValue, typeof(Acknowledge))]
public partial interface IMessage
{

}
