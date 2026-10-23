using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Platform.Proton.UserTools;

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
