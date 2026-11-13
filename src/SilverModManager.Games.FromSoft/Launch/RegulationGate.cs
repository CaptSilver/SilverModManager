using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Launch;

/// <summary>
/// Refuses a launch without mods of a game that plays online when its regulation is not a file Steam shipped. Starting
/// the game from Steam loads whatever is in the game folder, and a changed regulation is what the anti-cheat service
/// bans for. The veto says how to get the file back: Health Check's restore when the app has a baseline for it,
/// Steam's file verification otherwise. A modded launch never reads the game folder's regulation, so it is not checked.
/// Without a readable manifest there is nothing to compare with, and the launch goes ahead.
/// </summary>
/// <remarks>Independent of <see cref="VanillaSaveGate"/>: each gate looks at its own condition and vetoes on its own.</remarks>
public sealed class RegulationGate(
    IConnection connection,
    IManifestVouching vouching,
    RegulationRestore restore,
    BuildStampStore stamps,
    ILogger<RegulationGate> logger) : ILaunchGate
{
    /// <inheritdoc/>
    public Veto? Check(LaunchContext context)
    {
        var installation = context.Installation;
        if (installation.Game is not IFromSoftGame { HasOnlinePlay: true } game) return null;
        if (game.Formats.RegulationFileName is not { } regulationName) return null;
        var loadout = Loadout.Load(connection.Db, context.LoadoutId);
        if (game.Loader.IsModded(loadout)) return null;

        var regulation = FromSoftPaths.GameFolder(game.Layout, installation) / regulationName;
        // The interface is synchronous; hashing the regulation is quick, and the work runs off the caller's thread.
        if (Task.Run(() => FileSha1.Of(regulation, CancellationToken.None, logger)).GetAwaiter().GetResult() is not { } hash) return null;

        if (vouching.InstalledFiles(installation) is not { } manifest)
        {
            logger.LogInformation("Steam's manifest for {Game} could not be read, so its regulation was not checked before a launch without mods", game.DisplayName);
            return null;
        }

        if (vouching.Find(installation, hash) is not null) return null;

        var stamped = stamps.Find(loadout.InstallationId);
        var wayOut = restore.Candidate(installation, stamped, manifest) is not null
            ? "Restore the game's own regulation from Health Check first."
            : "Verify the game's files in Steam first.";
        var shown = FromSoftPaths.InGameFolder(game.Layout, regulationName).Path;
        return new Veto($"Cannot start {game.DisplayName} without mods: {shown} is not the file Steam shipped, and the game plays online. {wayOut}");
    }

    /// <inheritdoc/>
    public Task Prepare(LaunchContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Finish(LaunchContext context) => Task.CompletedTask;
}
