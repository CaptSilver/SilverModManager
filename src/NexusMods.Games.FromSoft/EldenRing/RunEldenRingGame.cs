using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.EldenRing;

/// <summary>
/// The launch tool of the game. A loadout with mods runs me3 inside a bwrap sandbox that overlays the game folder, so
/// nothing the game or a mod writes reaches the real folder; the tool's spec is that whole command. A loadout without
/// mods starts the game's own executable through Steam, with its anti-cheat, as for any game.
/// </summary>
public class RunEldenRingGame(IServiceProvider provider, EldenRingGame game) : RunGameTool<EldenRingGame>(provider, game)
{
    public override async ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        if (!ModdedLoadout.IsModded(loadout)) return await base.GetLaunchSpec(loadout, cancellationToken);

        // The loadout is checked, not the disk: a dry run resolves the command before anything is applied.
        if (!ModdedLoadout.HasHost(loadout))
            throw new InvalidOperationException($"Loadout '{loadout.Name}' has mods but no me3, so there is nothing to load them with. Install the me3 Linux release into it.");

        var bwrap = provider.GetRequiredService<IExecutableLocator>().Find(OverlayCommand.Program)
                    ?? throw new InvalidOperationException("bwrap was not found on PATH. It runs the game with an overlay over its folder, so a loadout with mods cannot start without it; install bubblewrap.");

        var installation = loadout.InstallationInstance;
        var ids = provider.GetRequiredService<IUserIds>();
        var host = installation.Locations[LocationId.Me3Host].Path;
        var argv = OverlayCommand.Build(
            LayoutOf(loadout), ids.Uid, ids.Gid,
            host / Me3HostFiles.Binary,
            installation.Locations[LocationId.Me3Mods].Path / Me3ModsLayout.ProfileFileName,
            host / "bin/win64");

        return new LaunchSpec(bwrap, argv[1..]) { ReplacesCommand = true, Environment = EldenRingPaths.Me3HomeEnvironment(Me3HomeOf(loadout)) };
    }

    /// <summary>
    /// A loadout with mods is started here, as a process of this app: <see cref="RunGameTool{T}.Execute"/> would hand
    /// bwrap to the Windows tool runner, since every Proton install counts as one, and going through Steam records the
    /// spec, which refuses an executable outside the game folder. The session takes the lock, applies the loadout and
    /// runs the gates around the process; a loadout without mods goes the ordinary way.
    /// </summary>
    /// <remarks>
    /// Not run against the real game: whether me3 needs the environment Steam gives a game it starts (STEAM_COMPAT_*,
    /// SteamAppId), which a process started from here does not have, is decided by a played session. The fallback is
    /// to record the replaced command and start the game through <c>steam://run</c>, as the run verb does.
    /// </remarks>
    public override async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken, string[]? commandLineArgs)
    {
        if (!ModdedLoadout.IsModded(loadout))
        {
            await base.Execute(loadout, cancellationToken, commandLineArgs);
            return;
        }

        if (commandLineArgs is { Length: > 0 })
            throw new InvalidOperationException($"Arguments for a game started through me3 are not supported: {string.Join(' ', commandLineArgs)}");

        var installation = loadout.InstallationInstance;
        var spec = await GetLaunchSpec(loadout, cancellationToken);
        var plan = LaunchPlanner.Compose(
            [], installation.Locations[LocationId.Game].Path,
            installation.Locations.ToAbsolutePath(game.GetPrimaryFile(installation)),
            spec, FilesystemRootsOf(loadout), WineDllOverrideRequirements.For(installation.Game, loadout));

        var processes = provider.GetRequiredService<IGameProcessFactory>();
        using var launch = provider.GetRequiredService<IGameLaunchSession>().BeginSession(loadout);
        await launch.RunAsync(async token =>
        {
            using var process = processes.Start(plan.Process, installation);
            try
            {
                await process.WaitAsync(token);
            }
            catch
            {
                // Never leave the game running unattended behind an exception or a cancelled wait.
                process.Terminate();
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// The host folders me3's own container must be able to write to. The deployment backends report nothing for a
    /// copy or link deployment, so the mods, the host files, the overlay's folder and me3's home are named here as well.
    /// </summary>
    internal IReadOnlyList<string> FilesystemRootsOf(Loadout.ReadOnly loadout)
    {
        var installation = loadout.InstallationInstance;
        var roots = new SortedSet<string>(provider.GetRequiredService<DeploymentLaunchRoots>().For(installation, loadout.InstallationId), StringComparer.Ordinal)
        {
            installation.Locations[LocationId.Me3Mods].Path.GetFullPath(),
            installation.Locations[LocationId.Me3Host].Path.GetFullPath(),
            LayoutOf(loadout).Upper.GetFullPath(),
            Me3HomeOf(loadout).GetFullPath(),
        };
        return roots.ToArray();
    }

    private AbsolutePath Me3HomeOf(Loadout.ReadOnly loadout)
        => EldenRingPaths.Me3Home(provider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>(), provider.GetRequiredService<IFileSystem>(),
            EldenRingPaths.InstallationFolderName(loadout.InstallationInstance.LocatorResult));

    private OverlayLayout LayoutOf(Loadout.ReadOnly loadout)
        => OverlayLayout.For(provider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>(), provider.GetRequiredService<IFileSystem>(), loadout.InstallationInstance, loadout.LoadoutId);
}
