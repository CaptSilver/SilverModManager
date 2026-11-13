using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.LoadOrderFile.Tests.Fixtures;

/// <summary>
/// Runs a test against one of the real load-order-file games, registered the way the app registers them, with the
/// library and installer helpers a test needs to put an archive through the game's installers.
/// </summary>
public abstract class ALoadOrderFileGameTests<TTest>(ITestOutputHelper outputHelper, LoadOrderFileGameDefinition definition)
    : ALibraryArchiveInstallerTests<TTest>(outputHelper, definition.GameId)
{
    protected LoadOrderFileGameDefinition Definition { get; } = definition;

    protected override IServiceCollection AddServices(IServiceCollection services)
        => AddGameLocator(base.AddServices(services).AddLoadOrderFileGames());

    /// <summary>
    /// Registers the one locator that finds the game, in a folder that holds the game's executable under the name the
    /// definition gives it.
    /// </summary>
    protected virtual IServiceCollection AddGameLocator(IServiceCollection services)
        => services.AddUniversalGameLocator(
            Definition.GameId,
            new Dictionary<RelativePath, byte[]> { [Definition.Executable] = [] },
            locationsFileSystem: IsolatedKnownFolders.Create,
            platform: OSPlatform.Windows);

    /// <summary>
    /// The "Location:path" targets the first installer of the game that supports the archive puts its files at, in
    /// ordinal order, or null when none does. The install job takes installers in order and keeps the first that
    /// succeeds; so does this.
    /// </summary>
    protected async Task<string[]?> InstallTargets(string[] archivePaths, string? archiveFileName = null)
    {
        var archive = archiveFileName is null ? await AddFromPaths(archivePaths) : await AddArchiveNamed(archiveFileName, archivePaths);
        return await InstallTargets(archive);
    }

    /// <summary>Like <see cref="InstallTargets(string[], string?)"/>, for an archive the test built.</summary>
    protected async Task<string[]?> InstallTargets(LibraryArchive.ReadOnly archive) => (await TryInstall(archive)).Targets;

    /// <summary>
    /// Like <see cref="InstallTargets(LibraryArchive.ReadOnly)"/>, plus the first reason an installer gave for not taking
    /// the archive, which is what the player reads when none of them does.
    /// </summary>
    protected async Task<(string[]? Targets, string? Reason)> TryInstall(LibraryArchive.ReadOnly archive)
    {
        var loadout = await CreateLoadout();
        var libraryItem = archive.AsLibraryFile().AsLibraryItem();
        string? reason = null;

        foreach (var installer in Game.LibraryItemInstallers)
        {
            using var tx = Connection.BeginTransaction();
            var group = new LoadoutItemGroup.New(tx, out var groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = libraryItem.Name, LoadoutId = loadout },
            };

            var result = await installer.ExecuteAsync(libraryItem, group, tx, loadout, CancellationToken.None);
            if (result.IsNotSupported(out var why))
            {
                reason ??= why;
                continue;
            }

            if (result.IsSuccess is false) continue;

            var committed = (await tx.Commit()).Remap(group);
            return (committed.Children.OfTypeLoadoutItemWithTargetPath()
                .Select(file => (GamePath)file.TargetPath)
                .Select(path => $"{path.LocationId}:{path.Path}")
                .Order(StringComparer.Ordinal)
                .ToArray(), null);
        }

        return (null, reason);
    }
}
