using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using Avalonia.Platform.Storage;
using SilverModManager.Abstractions.Library;
using SilverModManager.App.UI;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>A file the user picks for a manual collection download is recorded for the collection's target loadout's game.</summary>
public class ManualDownloadDestinationTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task APickedFile_IsRecordedForTheTargetLoadoutsGame()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Collection target");
        var game = loadout.InstallationInstance.Game.GameId;
        var root = Directory.CreateTempSubdirectory("manual-download-").FullName;
        try
        {
            var picked = Path.Combine(root, "manual.zip");
            await File.WriteAllTextAsync(picked, "manual " + Guid.NewGuid());
            var md5 = await Md5Hasher.HashAsync(FileSystem.Shared.FromUnsanitizedFullPath(picked).Read());

            var download = await SeedExternalDownload(md5);
            var interop = Substitute.For<IAvaloniaInterop>();
            interop.OpenFilePickerAsync(Arg.Any<FilePickerOpenOptions>()).Returns([FileSystem.Shared.FromUnsanitizedFullPath(picked)]);
            var services = new ServiceCollection().AddSingleton(interop).AddSingleton<IGameDomainToGameIdMappingCache>(new CyberpunkDomains()).BuildServiceProvider();
            var viewModel = new ManualDownloadRequiredOverlayViewModel(new OverlayProvider(Provider, services), download, loadout.LoadoutId, isEmbedded: false)
            {
                Controller = Substitute.For<IOverlayController>(),
            };

            viewModel.CommandAddFile.Execute(R3.Unit.Default);
            await Eventually(() => LibraryDestinations.ItemsFor(Connection.Db, game).Any(item => item.Name == "manual.zip"));

            LibraryDestinations.Unassigned(Connection.Db).Should().NotContain(item => item.Name == "manual.zip");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<CollectionDownloadExternal.ReadOnly> SeedExternalDownload(Md5Value md5)
    {
        using var tx = Connection.BeginTransaction();
        var author = new User.New(tx) { Name = "Author", NexusId = 987654 };
        var collection = new CollectionMetadata.New(tx)
        {
            CollectionId = CollectionId.From(987654),
            Slug = CollectionSlug.From("manual-" + Guid.NewGuid().ToString("N")),
            Name = "Manual",
            GameId = NexusModsGameId.From(3333),
            AuthorId = author,
        };
        var revision = new CollectionRevisionMetadata.New(tx)
        {
            RevisionId = RevisionId.From(987654),
            RevisionNumber = RevisionNumber.From(1),
            CollectionId = collection,
        };
        var download = new CollectionDownload.New(tx) { ArrayIndex = 0, CollectionRevisionId = revision, IsOptional = false, Name = "Manual mod" };
        var external = new CollectionDownloadExternal.New(tx, download.Id)
        {
            CollectionDownload = download, Md5 = md5, Size = Size.From(10), Uri = new Uri("https://example.invalid/manual.zip"),
        };
        var result = await tx.Commit();
        return CollectionDownloadExternal.Load(result.Db, result[external.Id]);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition did not hold within 30 seconds.");
            await Task.Delay(25);
        }
    }

    private sealed class CyberpunkDomains : IGameDomainToGameIdMappingCache
    {
        public GameDomain GetDomain(NexusModsGameId id) => GameDomain.From("cyberpunk2077");
        public NexusModsGameId GetId(GameDomain domain) => NexusModsGameId.From(3333);
    }

    private sealed class OverlayProvider(IServiceProvider host, ServiceProvider overrides) : IServiceProvider
    {
        public object? GetService(Type serviceType) => overrides.GetService(serviceType) ?? host.GetService(serviceType);
    }
}
