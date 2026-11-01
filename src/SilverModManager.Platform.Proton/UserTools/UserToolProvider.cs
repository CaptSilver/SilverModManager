using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.UserTools;

/// <summary>
/// Lists the programs the user added for a loadout's installation as tools.
/// </summary>
public sealed class UserToolProvider(IConnection connection, ModdingToolFactory factory) : IToolProvider
{
    /// <inheritdoc/>
    public IEnumerable<ITool> GetTools(Loadout.ReadOnly loadout)
    {
        var gameId = loadout.Game.GameId;
        foreach (var tool in UserTool.FindByInstallation(connection.Db, loadout.InstallationId))
        {
            var exe = FileSystem.Shared.FromUnsanitizedFullPath(tool.Exe);
            var definition = ToolDefinition.AdHoc(gameId, tool.Name, exe.FileName, tool.RunVia);
            yield return factory.Create(definition, new InstalledTool(exe, tool.Arguments, tool.RunVia), gameId);
        }
    }
}
