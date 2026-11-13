using System.Collections.Immutable;
using DynamicData.Kernel;
using SilverModManager.Sdk.IO;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using SilverModManager.Games.TestFramework;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Networking.NexusWebApi.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.CLI.Tests;

/// <summary>A game that shares Cyberpunk's Nexus id, the way Skyrim VR shares Skyrim SE's.</summary>
internal sealed class TwinGame : IGameData
{
    public GameId GameId => GameId.From("TwinOfCyberpunk");
    public StoreIdentifiers StoreIdentifiers => new(GameId);
    public string DisplayName => "Cyberpunk Twin";
    public Optional<NexusModsGameId> NexusModsGameId => NexusModsApiId;
    public IStreamFactory IconImage => throw new NotSupportedException();
    public IStreamFactory TileImage => throw new NotSupportedException();
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => ImmutableDictionary<LocationId, AbsolutePath>.Empty;
    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    private static readonly Optional<NexusModsGameId> NexusModsApiId = Sdk.NexusModsApi.NexusModsGameId.From(3333);
}

/// <summary>
/// The Nexus side of an install shared by the browse page and the verbs: which game a loadout is, a mod's files and
/// which library copy of a file an install uses. The GraphQL client is a fake, so nothing reaches the network.
/// </summary>
[Collection("ConsoleOutput")]
public class NexusModsInstallerTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<NexusModsInstallerTests>(helper)
{
    private readonly IGraphQlClient _graphQl = Substitute.For<IGraphQlClient>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services).AddSingleton(_graphQl).AddSingleton<IGameData>(new TwinGame());
    }

    private INexusModsInstaller Installer => ServiceProvider.GetRequiredService<INexusModsInstaller>();

    private static IModFile FileOf(NexusModsGameId game, uint id, string name, ModFileCategory category)
    {
        var file = Substitute.For<IModFile>();
        file.Uid.Returns(new FileUid(FileId.From(id), game).ToV2Api());
        file.Name.Returns(name);
        file.Version.Returns("1.0");
        file.Category.Returns(category);
        file.SizeInBytes.Returns("2048");
        file.Date.Returns(1_700_000_000);
        return file;
    }

    [Fact]
    public async Task Files_ListsEveryFileWithItsCategory()
    {
        var loadout = await CreateLoadout();
        var game = Installer.GameOf(loadout.LoadoutId, out _)!.Value;
        IModFile[] files = [FileOf(game, 1, "Core", ModFileCategory.Main), FileOf(game, 2, "Extras", ModFileCategory.Optional)];
        _graphQl.QueryModFiles(ModId.From(9), game, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new GraphQlResult<IModFile[], NotFound>(files)));

        var rows = await Installer.Files(game, ModId.From(9), CancellationToken.None);

        rows.Select(r => (r.Id.Value, r.Name, r.Category)).Should().Equal(
            (1u, "Core", NexusModsFileCategory.Main), (2u, "Extras", NexusModsFileCategory.Optional));
        rows[0].SizeBytes.Should().Be(2048);
        rows[0].Date.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    }

    [Fact]
    public async Task Files_ThrowsWhenNexusDoesNotAnswer()
    {
        var game = NexusModsGameId.From(3333);
        var outage = new UnknownError { Code = ErrorCode.From("SMM_UNKNOWN_ERROR"), Message = "bad gateway", ClientError = Substitute.For<StrawberryShake.IClientError>() };
        _graphQl.QueryModFiles(default, default, default)
            .ReturnsForAnyArgs(ValueTask.FromResult(new GraphQlResult<IModFile[], NotFound>([new KeyValuePair<ErrorCode, IGraphQlError>(outage.Code, outage)])));

        var act = async () => await Installer.Files(game, ModId.From(9), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().StartWith("Nexus Mods did not answer");
    }

    [Fact]
    public async Task Files_IsEmptyForAModNexusDoesNotKnow()
    {
        _graphQl.QueryModFiles(default, default, default)
            .ReturnsForAnyArgs(ValueTask.FromResult(new GraphQlResult<IModFile[], NotFound>(new NotFound { Message = "no such mod" })));

        (await Installer.Files(NexusModsGameId.From(3333), ModId.From(9), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task GameOf_NamesTheGameThatSharesTheId()
    {
        var loadout = await CreateLoadout();

        Installer.GameOf(loadout.LoadoutId, out var sharedWith).Should().Be(NexusModsGameId.From(3333));

        sharedWith.Should().Be("Cyberpunk Twin");
    }

    [Fact]
    public void GameOf_IsNullWithoutALoadout()
    {
        Installer.GameOf(default, out var sharedWith).Should().BeNull();
        sharedWith.Should().BeNull();
    }

    // A library file the way a finished Nexus download leaves it: the item carries the file's metadata.
    private async Task<LibraryItem.ReadOnly> SeedLibraryFile(NexusModsGameId game, uint modId, uint fileId)
    {
        using var tx = Connection.BeginTransaction();
        var existingPage = NexusModsModPageMetadata.FindByUid(Connection.Db, new ModUid(ModId.From(modId), game)).FirstOrDefault();
        var page = existingPage.IsValid()
            ? null
            : new NexusModsModPageMetadata.New(tx)
            {
                Uid = new ModUid(ModId.From(modId), game),
                Name = "Some mod",
                GameDomain = GameDomain.From("cyberpunk2077"),
                UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            };
        EntityId pageId = existingPage.IsValid() ? existingPage.Id : page!.Id;
        var existingFile = NexusModsFileMetadata.FindByUid(Connection.Db, new FileUid(FileId.From(fileId), game)).FirstOrDefault();
        EntityId fileMetadataId;
        if (existingFile.IsValid()) fileMetadataId = existingFile.Id;
        else
        {
            fileMetadataId = new NexusModsFileMetadata.New(tx)
            {
                Uid = new FileUid(FileId.From(fileId), game),
                Name = "Some mod.zip",
                Version = "1",
                UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                ModPageId = pageId,
            }.Id;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes($"Some mod-{Guid.NewGuid()}");
        var item = new NexusModsLibraryItem.New(tx, out var id)
        {
            LibraryItem = new LibraryItem.New(tx, id) { Name = "Some mod.zip" },
            FileMetadataId = fileMetadataId,
            ModPageMetadataId = pageId,
        };
        _ = new LibraryFile.New(tx, id)
        {
            FileName = "Some mod.zip",
            Hash = bytes.xxHash3(),
            Size = Size.FromLong(bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = "Some mod.zip" },
        };
        var result = await tx.Commit();
        return result.Remap(item).AsLibraryItem();
    }

    private async Task LinkIntoLoadout(LoadoutId loadout, LibraryItem.ReadOnly item)
    {
        using var tx = Connection.BeginTransaction();
        var parent = DefaultCollection.Create(tx, loadout);
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { LoadoutId = loadout, Name = "Some mod", ParentId = LoadoutItemGroupId.From(parent.Id) },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, groupId) { LibraryItemId = item.Id, LoadoutItemGroup = group };
        await tx.Commit();
    }

    // The host's database is shared by every test in the run, so each test seeds ids no other test uses.
    private static uint _lastId = 50_000;
    private static uint NextId() => Interlocked.Increment(ref _lastId);

    [Fact]
    public async Task FindInLibrary_PrefersTheCopyLinkedIntoTheLoadout()
    {
        var loadout = await CreateLoadout();
        var game = Installer.GameOf(loadout.LoadoutId, out _)!.Value;
        var (mod, file) = (NextId(), NextId());
        await SeedLibraryFile(game, mod, file);
        var linked = await SeedLibraryFile(game, mod, file);
        await SeedLibraryFile(game, mod, file);
        await LinkIntoLoadout(loadout.LoadoutId, linked);

        var found = Installer.FindInLibrary(game, ModId.From(mod), FileId.From(file), loadout.LoadoutId);

        found!.Value.Id.Should().Be(linked.Id);
    }

    [Fact]
    public async Task FindInLibrary_IsNullWhenTheLibraryLacksTheFile()
    {
        var loadout = await CreateLoadout();
        var game = Installer.GameOf(loadout.LoadoutId, out _)!.Value;

        Installer.FindInLibrary(game, ModId.From(NextId()), FileId.From(NextId()), loadout.LoadoutId).Should().BeNull();
    }

    [Fact]
    public async Task Resolve_UsesTheLibraryCopyWhenThereIsOne()
    {
        var loadout = await CreateLoadout();
        var game = Installer.GameOf(loadout.LoadoutId, out _)!.Value;
        var (mod, file) = (NextId(), NextId());
        var item = await SeedLibraryFile(game, mod, file);

        var resolved = Installer.Resolve(game, ModId.From(mod), FileId.From(file), "Some mod", dependency: false, loadout.LoadoutId);

        resolved.InLibrary!.Value.Id.Should().Be(item.Id);
        resolved.Name.Should().Be("Some mod");
        resolved.Dependency.Should().BeFalse();
    }

    [Fact]
    public async Task Resolve_HasNothingInTheLibraryForAFileItLacks()
    {
        var loadout = await CreateLoadout();
        var game = Installer.GameOf(loadout.LoadoutId, out _)!.Value;

        Installer.Resolve(game, ModId.From(NextId()), FileId.From(NextId()), "Some mod", dependency: true, loadout.LoadoutId)
            .Should().Match<ResolvedInstall>(r => r.InLibrary == null && r.Dependency);
    }

    [Fact]
    public void DownloadPage_IsTheFilesTabLinkWithTheOneClickButton()
    {
        var page = Installer.DownloadPage(NexusModsGameId.From(3333), ModId.From(7), FileId.From(55));

        page.AbsolutePath.Should().EndWith("/mods/7");
        page.Query.Should().Be("?tab=files&file_id=55&nmm=1");
    }

    [Fact]
    public async Task ModOf_ReadsTheModFromTheFileQuery()
    {
        var game = NexusModsGameId.From(3333);
        var file = Substitute.For<IModFile>();
        file.ModId.Returns(77);
        _graphQl.QueryModFile(FileId.From(5), game, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new GraphQlResult<IModFile, NotFound>(file)));

        (await Installer.ModOf(game, FileId.From(5), CancellationToken.None)).Should().Be(ModId.From(77));
    }

    [Fact]
    public async Task ModOf_ThrowsForAFileNexusDoesNotKnow()
    {
        _graphQl.QueryModFile(default, default, default)
            .ReturnsForAnyArgs(ValueTask.FromResult(new GraphQlResult<IModFile, NotFound>(new NotFound { Message = "no such file" })));

        var act = async () => await Installer.ModOf(NexusModsGameId.From(3333), FileId.From(5), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

/// <summary>A loadout of a game that is not on Nexus Mods has no Nexus game.</summary>
[Collection("ConsoleOutput")]
public class NexusModsInstallerNoNexusGameTests(ITestOutputHelper helper) : AIsolatedGameTest<NexusModsInstallerNoNexusGameTests, ThunderstoreStubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<ThunderstoreStubbedGame>()
            .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"));
        return services;
    }

    [Fact]
    public async Task GameOf_IsNullForAGameNotOnNexus()
    {
        var loadout = await CreateLoadout();

        ServiceProvider.GetRequiredService<INexusModsInstaller>().GameOf(loadout.LoadoutId, out var sharedWith).Should().BeNull();
        sharedWith.Should().BeNull();
    }
}
