using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft;

/// <summary>
/// The launch tool of a FromSoft game. A loadout with mods runs the game's mod loader inside a bwrap sandbox that
/// overlays the game folder, so nothing the game or a mod writes reaches the real folder; the tool's spec is that whole
/// command. A loadout without mods starts the game's own executable through Steam, with its anti-cheat, as for any game.
/// </summary>
public class RunFromSoftGame<TGame>(IServiceProvider provider, TGame game) : RunGameTool<TGame>(provider, game)
    where TGame : AFromSoftGame<TGame>, IGameData<TGame>
{
    public override async ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        if (!game.Loader.IsModded(loadout)) return await base.GetLaunchSpec(loadout, cancellationToken);

        // The loadout is checked, not the disk: a dry run resolves the command before anything is applied.
        if (!game.Loader.HasHost(loadout))
            throw new InvalidOperationException($"Loadout '{loadout.Name}' has mods but no me3, so there is nothing to load them with. Install the me3 Linux release into it.");

        var bwrap = provider.GetRequiredService<IExecutableLocator>().Find(OverlayCommand.Program)
                    ?? throw new InvalidOperationException("bwrap was not found on PATH. It runs the game with an overlay over its folder, so a loadout with mods cannot start without it; install bubblewrap.");

        var installation = loadout.InstallationInstance;
        var ids = provider.GetRequiredService<IUserIds>();
        var argv = OverlayCommand.Build(LayoutOf(loadout), ids.Uid, ids.Gid, game.Loader.LaunchCommand(installation));

        return new LaunchSpec(bwrap, argv[1..]) { ReplacesCommand = true, Environment = game.Loader.LaunchEnvironment(installation).ToDictionary() };
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
        if (!game.Loader.IsModded(loadout))
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
    /// The host folders the loader's own container must be able to write to. The deployment backends report nothing for a
    /// copy or link deployment, so the loader's folders and the overlay's are named here as well.
    /// </summary>
    internal IReadOnlyList<string> FilesystemRootsOf(Loadout.ReadOnly loadout)
    {
        var installation = loadout.InstallationInstance;
        var roots = new SortedSet<string>(provider.GetRequiredService<DeploymentLaunchRoots>().For(installation, loadout.InstallationId), StringComparer.Ordinal)
        {
            LayoutOf(loadout).Upper.GetFullPath(),
        };
        foreach (var root in game.Loader.WritableRoots(installation)) roots.Add(root.GetFullPath());
        return roots.ToArray();
    }

    private OverlayLayout LayoutOf(Loadout.ReadOnly loadout)
        => OverlayLayout.For(provider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>(), provider.GetRequiredService<IFileSystem>(), loadout.InstallationInstance, loadout.LoadoutId);
}
