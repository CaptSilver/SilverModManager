using MemoryPack;

namespace SilverModManager.ProxyConsole.Messages;

/// <summary>
/// How an accepted launch ended.
/// </summary>
[MemoryPackable]
public partial class LaunchExited : IMessage
{
    public required int ExitCode { get; init; }

    /// <summary>Why the launch failed; null when the game ran and <see cref="ExitCode"/> is its own.</summary>
    public string? Message { get; init; }
}
