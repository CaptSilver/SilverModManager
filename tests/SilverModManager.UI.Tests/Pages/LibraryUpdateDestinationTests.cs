using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// An update of a library file is for the games the old file was for, not for the game of the page that started the
/// update: a Nexus Mods page is shared by every game with its id.
/// </summary>
public class LibraryUpdateDestinationTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly GameId OtherGame = GameId.From("UpdateTestSiblingGame");

    private sealed class CompletedDownloadJob(AbsolutePath path) : IDownloadJob, IJobDefinitionWithStart<CompletedDownloadJob, AbsolutePath>
    {
        public AbsolutePath Destination => path;
        public ValueTask<AbsolutePath> StartAsync(IJobContext<CompletedDownloadJob> context) => ValueTask.FromResult(path);
        public ValueTask AddMetadata(ITransaction tx, LibraryFile.New libraryFile) => ValueTask.CompletedTask;
    }

    /// <summary>A Nexus Mods library item for a file, with the games it is for.</summary>
    private async Task<(NexusModsFileMetadata.ReadOnly File, EntityId Item)> SeedNexusItem(Destination destination, uint number)
    {
        using var tx = Connection.BeginTransaction();
        var page = new NexusModsModPageMetadata.New(tx)
        {
            Uid = new ModUid(ModId.From(number), NexusModsGameId.From(1704)),
            Name = "Shared by SE and VR",
            GameDomain = GameDomain.From("skyrimspecialedition"),
            UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
        };
        var metadata = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(number), NexusModsGameId.From(1704)),
            Name = "old.zip",
            Version = "1",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            ModPageId = page,
        };
        var item = new NexusModsLibraryItem.New(tx, out var id)
        {
            FileMetadataId = metadata.Id,
            ModPageMetadataId = page.Id,
            LibraryItem = new LibraryItem.New(tx, id) { Name = "old.zip" },
        };
        LibraryDestinations.Write(tx, id, destination);
        var result = await tx.Commit();
        return (NexusModsFileMetadata.Load(result.Db, result[metadata.Id]), result[id]);
    }

    [Fact]
    public async Task UpdateAndReplace_NewFile_KeepsTheOldFilesGames_NotThePagesGame()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "VR page");
        var pageGame = loadout.InstallationInstance.Game.GameId;
        var (oldFile, oldItem) = await SeedNexusItem(new Destination(new HashSet<GameId> { OtherGame, GameId.From("UpdateTestOtherSibling") }, Shared: true), number: 910);
        var vm = new LibraryViewModel(Substitute.For<IWindowManager>(), Provider, Provider.GetRequiredService<IGameDomainToGameIdMappingCache>(), loadout.LoadoutId);
        var path = Path.Combine(Directory.CreateTempSubdirectory("update-destination-").FullName, "new.zip");
        await File.WriteAllTextAsync(path, Guid.NewGuid().ToString());
        var job = Provider.GetRequiredService<IJobMonitor>()
            .Begin<CompletedDownloadJob, AbsolutePath>(new CompletedDownloadJob(FileSystemPath(path)));

        var added = await vm.AddUpdatedFile(job, [oldFile]);

        var item = LibraryItem.Load(Connection.Db, added.Id);
        LibraryDestinations.GamesOf(item).Should().BeEquivalentTo([OtherGame, GameId.From("UpdateTestOtherSibling")])
            .And.NotContain(pageGame);
        item.Contains(LibraryItem.DestinationsShared).Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAndKeepOld_NewFile_OfAnUnassignedOldFile_IsUnassigned()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Page of some game");
        var (oldFile, _) = await SeedNexusItem(Destination.Unknown, number: 911);
        var vm = new LibraryViewModel(Substitute.For<IWindowManager>(), Provider, Provider.GetRequiredService<IGameDomainToGameIdMappingCache>(), loadout.LoadoutId);
        var path = Path.Combine(Directory.CreateTempSubdirectory("update-destination-").FullName, "new.zip");
        await File.WriteAllTextAsync(path, Guid.NewGuid().ToString());
        var job = Provider.GetRequiredService<IJobMonitor>()
            .Begin<CompletedDownloadJob, AbsolutePath>(new CompletedDownloadJob(FileSystemPath(path)));

        var added = await vm.AddUpdatedFile(job, [oldFile]);

        var item = LibraryItem.Load(Connection.Db, added.Id);
        LibraryDestinations.GamesOf(item).Should().BeEmpty();
        item.Contains(LibraryItem.DestinationUnknown).Should().BeTrue();
    }

    private static AbsolutePath FileSystemPath(string path) => FileSystem.Shared.FromUnsanitizedFullPath(path);
}
