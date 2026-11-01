using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;
using SilverModManager.App.UI.Windows;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>Files added from the Library page's "add from disk" are recorded for the page's loadout's game.</summary>
public class LibraryAddFromDiskTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task FilesAddedFromDisk_AreRecordedForTheLoadoutsGame()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Add from disk");
        var game = loadout.InstallationInstance.Game.GameId;
        var path = Path.Combine(Directory.CreateTempSubdirectory("add-from-disk-").FullName, "from-disk.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("from-disk.txt").Open());
            writer.Write("from-disk");
        }
        var vm = new LibraryViewModel(
            Substitute.For<IWindowManager>(), Provider, Provider.GetRequiredService<IGameDomainToGameIdMappingCache>(), loadout.LoadoutId);

        await vm.AddFilesToLibrary([FileSystem.Shared.FromUnsanitizedFullPath(path)], CancellationToken.None);

        LibraryDestinations.ItemsFor(Connection.Db, game).Should().ContainSingle(item => item.Name == "from-disk.zip");
    }
}
