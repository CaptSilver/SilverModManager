using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.UserTools;

/// <summary>
/// Lists the programs the user added for a loadout's installation as tools.
/// </summary>
public sealed class UserToolProvider(IConnection connection, IToolRunner toolRunner, IProcessRunner processRunner) : IToolProvider
{
    /// <inheritdoc/>
    public IEnumerable<ITool> GetTools(Loadout.ReadOnly loadout)
    {
        var gameId = loadout.Game.GameId;
        foreach (var tool in UserTool.FindByInstallation(connection.Db, loadout.InstallationId))
        {
            yield return new UserToolAdapter(
                gameId,
                tool.Name,
                FileSystem.Shared.FromUnsanitizedFullPath(tool.Exe),
                tool.Arguments,
                tool.RunVia,
                toolRunner,
                processRunner);
        }
    }
}
