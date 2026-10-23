using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Steam;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Backend;
using SilverModManager.Games.FileHashes;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Networking.Steam;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// A game found the way the app finds it on the dev machine: the real Steam locator reads an app manifest from
/// a library behind a symlink, the real hashes service builds a baseline from depotcache manifests, and nothing
/// is stubbed between the game folder and the diagnostics. The Steam client, the library and the game all
/// live in <see cref="Library"/>, which has no files of anyone's real Steam.
/// </summary>
public abstract class ASteamInstalledGameTest<TTest, TGame>(ITestOutputHelper helper) : AIsolatedGameTest<TTest, TGame>(helper)
    where TGame : IGame
{
    // Initialised before the base constructor builds the service graph, which needs the file system it provides.
    protected SteamLibraryFixture Library { get; } = new();

    protected FullHashCounter FullHashes { get; } = new();

    /// <summary>The game as <see cref="SteamLibraryFixture.AddGame"/> installed it; set by <see cref="InstallGame"/>.</summary>
    protected SteamInstall Install => _install ?? throw new InvalidOperationException("The game is installed when the test is initialised");

    private SteamInstall? _install;

    protected AbsolutePath GameFolder => GameInstallation.Locations[LocationId.Game].Path;

    protected override bool UseStubbedFileHashService => false;

    protected override AbsolutePath TemporaryFilesPrefix => Library.Root / "tmp";

    /// <summary>Installs the game into <see cref="Library"/> before the locators run.</summary>
    protected abstract SteamInstall InstallGame(SteamLibraryFixture library);

    protected override ValueTask PrepareGameInstall()
    {
        // GameFinder looks for a Steam client under $XDG_DATA_HOME itself, bypassing the file system the locator
        // was given. scripts/test.sh points it at an empty folder; anywhere else it could be the real one.
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgDataHome) && Directory.Exists(Path.Combine(xdgDataHome, "Steam")))
            throw new InvalidOperationException($"'{xdgDataHome}/Steam' exists, and the Steam locator would read it. Run the tests through scripts/test.sh, which isolates XDG_DATA_HOME.");

        _install = InstallGame(Library);
        return ValueTask.CompletedTask;
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddDiagnosticWriter()
            .AddFullHashCounter(FullHashes)
            .AddGameLocators();
        services.RemoveAll<IFileSystem>();
        services.AddSingleton(Library.FileSystem);
        services.TryAddSingleton<IDepotManifestSource, LocalDepotManifests>();
        return services;
    }

    /// <summary>Locates the game again after the library changed on disk.</summary>
    protected void Relocate()
    {
        GameRegistry.ClearCache();
        GameInstallation = GameRegistry.LocateGameInstallations().Single(i => i.Game is TGame);
    }

    /// <summary>
    /// Runs every diagnostic emitter the app runs for the loadout and returns what they found. Unlike the diagnostic
    /// manager, which logs an emitter that throws and carries on, an exception here fails the test.
    /// </summary>
    protected async Task<Diagnostic[]> RunEveryEmitter(Loadout.ReadOnly loadout)
    {
        var syncTree = (await Synchronizer.BuildSyncTree(loadout)).ToFrozenDictionary();
        var emitters = GameInstallation.GetGame().DiagnosticEmitters
            .Concat(ServiceProvider.GetServices<IDiagnosticEmitter>())
            .OfType<ILoadoutDiagnosticEmitter>();

        var found = new List<Diagnostic>();
        foreach (var emitter in emitters)
        {
            await foreach (var diagnostic in emitter.Diagnose(loadout, syncTree, CancellationToken.None))
                found.Add(diagnostic);
        }

        return found.OrderBy(d => d.Id).ToArray();
    }

    /// <summary>A diagnostic as the Health Check page shows it: summary, then details.</summary>
    protected string Describe(Diagnostic diagnostic)
    {
        var writer = ServiceProvider.GetRequiredService<IDiagnosticWriter>();
        return $"{diagnostic.FormatSummary(writer)}\n{diagnostic.FormatDetails(writer)}";
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // The host keeps its databases under the library's folder, so the folder goes only once the host has let
        // go of it. A shutdown that failed or timed out has nothing more to release.
        await Task.WhenAny(HostShutdown);
        Library.Dispose();
    }
}
