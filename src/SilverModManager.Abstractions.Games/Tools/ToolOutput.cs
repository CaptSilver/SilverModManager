using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>Where a tool's results end up, and so how they are captured into the loadout.</summary>
public abstract record ToolOutput
{
    private ToolOutput() { }

    /// <summary>The tool produces nothing the loadout keeps.</summary>
    public static ToolOutput None { get; } = new NoOutput();

    /// <summary>
    /// The tool writes into the game folder. What changed there during the run is moved into the group called
    /// <see cref="GroupName"/>.
    /// </summary>
    public sealed record GameFolder(string GroupName) : ToolOutput;

    /// <summary>
    /// The tool writes to a folder it is given. The folder's files become the group called <see cref="GroupName"/>,
    /// placed under <see cref="MapsTo"/>.
    /// </summary>
    public sealed record DeclaredFolder(string GroupName, GamePath MapsTo) : ToolOutput;

    private sealed record NoOutput : ToolOutput;
}
