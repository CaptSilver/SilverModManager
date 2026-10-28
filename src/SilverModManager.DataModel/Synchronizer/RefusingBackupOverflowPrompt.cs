using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.DataModel.Synchronizer;

/// <summary>
/// The prompt for hosts that have no one to ask: headless runs and tests refuse, and the synchronizer turns that
/// into the readable <see cref="BackupOverflowException"/>.
/// </summary>
public sealed class RefusingBackupOverflowPrompt : IBackupOverflowPrompt
{
    /// <inheritdoc />
    public ValueTask<BackupOverflowChoice> Ask(GameInstallation installation, BackupOverflow overflow, CancellationToken cancellationToken)
        => ValueTask.FromResult(BackupOverflowChoice.Refuse);
}
