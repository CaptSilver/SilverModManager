using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// Starts OpenMW through the program of the config root the installation was found in, not through a file of the
/// Morrowind folder. The engine reads the user's openmw.cfg of its own root, so it is started with no arguments.
/// </summary>
public class OpenMwRunGameTool(IServiceProvider serviceProvider, OpenMw game) : RunGameTool<OpenMw>(serviceProvider, game)
{
    private readonly IOpenMwConfigRoots _roots = serviceProvider.GetRequiredService<IOpenMwConfigRoots>();

    public override ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var located = loadout.InstallationInstance.LocatorResult;
        var root = _roots.Roots.FirstOrDefault(candidate => candidate.ConfigFolder.GetFullPath() == located.StoreIdentifier)
            ?? throw new OpenMwProgramMissingException($"{located.StoreIdentifier} is not one of OpenMW's config folders, so the app does not know which OpenMW to start.");

        // flatpak has to be told to run the engine itself, and what that command does when the engine exits has not
        // been confirmed, so the app does not guess at a wait it cannot get right.
        if (root.Store == GameStore.OpenMWFlatpak)
            throw new OpenMwProgramMissingException("Starting the Flatpak build of OpenMW from the app is not supported yet. Start it from the OpenMW launcher.");

        var program = root.ResolveProgram()
            ?? throw new OpenMwProgramMissingException("OpenMW is not installed, so there is nothing to start.");
        return ValueTask.FromResult(new LaunchSpec(program, []));
    }

    // The program is the engine itself, and nothing else of that name is the game.
    protected override IReadOnlySet<string> TrackedProcessNames(AbsolutePath program, AbsolutePath primaryFile) =>
        new HashSet<string> { "openmw" };
}

/// <summary>The app cannot say what to start for an OpenMW installation.</summary>
public sealed class OpenMwProgramMissingException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "OpenMW cannot be started";
}
