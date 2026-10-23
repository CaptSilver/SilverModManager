using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Build;

/// <summary>
/// Stamps the installed build the first time the game is launched through the app, so a later change to it can be
/// told from the first one. Never refuses a launch; a build that cannot be read fails the prepare step instead of
/// being stamped half known. A stamp that predates the regulation fields gets them from the live build once, if the
/// install has not moved. Does nothing for another game.
/// </summary>
public sealed class BuildStampGate(IConnection connection, BuildStampStore stamps) : ILaunchGate
{
    /// <inheritdoc/>
    public Veto? Check(LaunchContext context) => null;

    /// <inheritdoc/>
    public async Task Prepare(LaunchContext context, CancellationToken cancellationToken)
    {
        if (context.Installation.Game is not IFromSoftGame) return;

        var installation = Loadout.Load(connection.Db, context.LoadoutId).InstallationId;
        // Identified first, so an unreadable build fails the launch even when a stamp exists.
        var live = BuildIdentity.Identify(context.Installation);
        var stamped = stamps.Find(installation);
        if (stamped is null)
        {
            await stamps.Record(installation, live);
        }
        // A stamp from before the regulation was tracked is completed while the install is unchanged, so the regulation
        // it is first compared with is the one that was there when nothing had moved.
        else if (stamped.Exe == live.Exe && stamped.SteamBuildId == live.SteamBuildId && BuildIdentity.CanBackfill(stamped, live))
        {
            await stamps.Backfill(installation, live);
        }
    }

    /// <inheritdoc/>
    public Task Finish(LaunchContext context) => Task.CompletedTask;
}
