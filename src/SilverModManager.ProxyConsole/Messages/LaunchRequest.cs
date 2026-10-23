using MemoryPack;

namespace SilverModManager.ProxyConsole.Messages;

/// <summary>
/// Sent instead of the program arguments by a client that wants the main process to run a game launch.
/// </summary>
[MemoryPackable]
public partial class LaunchRequest : IMessage
{
    /// <summary>Proves the client can read the main process's token file, so it is the same user.</summary>
    public required string Token { get; init; }

    public required string LoadoutName { get; init; }

    public string? Game { get; init; }

    /// <summary>The command after <c>--</c>, as Steam expanded <c>%command%</c>.</summary>
    public required string[] Command { get; init; }

    /// <summary>The environment the client was started with; the game is started in it, not in the main process's own.</summary>
    public required Dictionary<string, string> Environment { get; init; }

    public string? WorkingDirectory { get; init; }
}
