using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Platform.Proton.UserTools;

/// <summary>Reads a stored tool row the way both tool providers need it.</summary>
internal static class UserToolRows
{
    public static InstalledTool ToInstalledTool(this UserTool.ReadOnly row, AbsolutePath exe)
        => new(exe, row.Arguments, row.RunVia, row.OutputGroupName.ValueOrDefault());

    public static bool IsCatalogueTool(this UserTool.ReadOnly row) => row.ToolId.HasValue;
}
