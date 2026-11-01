using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.UserTools;

/// <summary>Reads a stored tool row the way both tool providers need it.</summary>
internal static class UserToolRows
{
    // The row stores a value for both, so a blank line and Auto stand for "not set" and leave the definition's
    // own arguments and run strategy in charge. A hand-added program's definition has neither, so nothing changes for it.
    public static InstalledTool ToInstalledTool(this UserTool.ReadOnly row, AbsolutePath exe)
        => new(exe,
            string.IsNullOrWhiteSpace(row.Arguments) ? null : row.Arguments,
            row.RunVia is ToolRunVia.Auto ? null : row.RunVia,
            row.OutputGroupName.ValueOrDefault());

    public static bool IsCatalogueTool(this UserTool.ReadOnly row) => row.ToolId.HasValue;
}
