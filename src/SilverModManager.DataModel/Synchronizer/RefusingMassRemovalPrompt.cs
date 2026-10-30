using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.DataModel.Synchronizer;

/// <summary>
/// The prompt for hosts that have no one to ask: headless runs and tests say no, and the synchronizer turns that
/// into the readable <see cref="MassRemovalException"/>.
/// </summary>
public sealed class RefusingMassRemovalPrompt : IMassRemovalPrompt
{
    /// <inheritdoc />
    public ValueTask<bool> Confirm(GameInstallation installation, MassRemoval removal, CancellationToken cancellationToken)
        => ValueTask.FromResult(false);
}
