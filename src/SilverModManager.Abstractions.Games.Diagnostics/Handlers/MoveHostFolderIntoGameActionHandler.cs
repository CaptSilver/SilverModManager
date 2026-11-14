using System.Collections.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.Abstractions.Diagnostics;

/// <summary>
/// Folds the copy of a game's folder on the host into the one inside its Proton prefix and sends the host copy to
/// the trash. The two paths come from the diagnostic's action arguments (host, then prefix). The diagnostic's note
/// already says what the button does, so there is no extra question. A refusal is thrown for the details page to show.
/// </summary>
[UsedImplicitly]
public sealed class MoveHostFolderIntoGameActionHandler(IServiceProvider serviceProvider, ITrash trash, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.MoveHostFolderIntoGameId;

    /// <inheritdoc />
    public bool RunsUnattended => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        if (arguments.Length != 2) throw new ArgumentException("Expected the host folder and the folder in the prefix.", nameof(arguments));
        var (hostPath, prefixPath) = (arguments[0], arguments[1]);

        var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");

        var installation = loadout.InstallationInstance;
        if (serviceProvider.GetRequiredService<IGameLaunchSession>().Describe(installation) is { } why)
        {
            throw new HostFolderMoveRefusedException(why is RunningInThisProcess
                ? $"It looks like {installation.Game.DisplayName} is already running.\nTo apply changes or launch the game again, please close the game first."
                : why.Message);
        }

        await Task.Run(() => HostFolderMove.Run(hostPath, prefixPath, trash));
        await diagnosticManager.RefreshAsync(loadoutId);
    }
}
