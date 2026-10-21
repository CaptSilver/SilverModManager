using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Build;

/// <summary>
/// Stamps the installed build the first time the game is launched through the app, so a later change to it can be
/// told from the first one. Never refuses a launch; a build that cannot be read fails the prepare step instead of
/// being stamped half known. Does nothing for another game.
/// </summary>
public sealed class BuildStampGate(IConnection connection, BuildStampStore stamps) : ILaunchGate
{
    /// <inheritdoc/>
    public Veto? Check(LaunchContext context) => null;

    /// <inheritdoc/>
    public async Task Prepare(LaunchContext context, CancellationToken cancellationToken)
    {
        if (context.Installation.Game.GameId != EldenRingGame.GameId) return;

        var installation = Loadout.Load(connection.Db, context.LoadoutId).InstallationId;
        // Identified first, so an unreadable build fails the launch even when a stamp exists.
        var live = BuildIdentity.Identify(context.Installation);
        if (stamps.Find(installation) is null) await stamps.Record(installation, live);
    }

    /// <inheritdoc/>
    public Task Finish(LaunchContext context) => Task.CompletedTask;
}
