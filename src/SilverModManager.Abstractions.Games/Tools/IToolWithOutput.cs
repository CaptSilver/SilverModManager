namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>
/// Implemented by a tool whose results the loadout keeps. The tool manager reads it to look after the tool's
/// group around a run; the capture itself is done by whoever knows where the output is.
/// </summary>
public interface IToolWithOutput
{
    /// <summary>Where the tool's results end up.</summary>
    ToolOutput Output { get; }

    /// <summary>
    /// Whether the previous run's group is switched off while the tool runs. A tool that regenerates everything
    /// would otherwise read its own old output as input.
    /// </summary>
    bool DisableOwnOutputDuringRun { get; }
}
