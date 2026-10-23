using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>
/// Installs one synthetic archive through the game's own installers and reports where each file lands.
/// One archive per layout: the wrapper-folder rule only applies when the archive has a single top folder.
/// </summary>
public abstract class ABethesdaInstallerRoutingTest<TTest, TGame>(ITestOutputHelper helper) : AIsolatedGameTest<TTest, TGame>(helper)
    where TTest : class
    where TGame : class, IGame
{
    protected abstract Version GameVersion { get; }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddCreationEngine()
            .AddUniversalGameLocator<TGame>(GameVersion, locationsFileSystem: IsolatedKnownFolders.Create);
    }

    /// <summary>The installed targets as "Location/path", sorted, so a test reads as one list.</summary>
    protected async Task<string[]> InstallLayout(params string[] archiveEntries)
    {
        var loadout = await CreateLoadout();
        var archive = await CreateTestArchive(archiveEntries.ToDictionary(e => RelativePath.FromUnsanitizedInput(e), _ => "x"u8.ToArray()));
        var library = await RegisterLocalArchive(archive.Path);

        var result = await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);

        return result.LoadoutItemGroup!.Value.Children.OfTypeLoadoutItemWithTargetPath()
            .Select(f => ((GamePath)f.TargetPath))
            .Select(p => $"{p.LocationId}/{p.Path}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
