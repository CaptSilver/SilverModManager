using FluentAssertions;
using SilverModManager.Sdk.Games;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Library.ModIo;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.GitHub;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Library.Tests.ModIo;

public class ModIoCatalogTests(IServiceProvider serviceProvider, IConnection connection)
{
    // Each test gets its own bytes: the library keeps one file per set of bytes and the tests share a database.
    private readonly byte[] _payload = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 + 3))).ToArray();
    private readonly uint _modId = (uint)Random.Shared.Next(1_000_000, 900_000_000);

    private CatalogQuery Query(ModIoCatalog catalog, string text = "", string? sort = null, string? cursor = null, bool adult = false)
        => new(text, sort is null ? catalog.Sorts[0] : catalog.Sorts.Single(s => s.Key == sort), cursor, adult);

    private ModIoCatalog CatalogOf(ModIoRig rig, out ILoadoutInstallCore core)
    {
        core = Substitute.For<ILoadoutInstallCore>();
        var installCore = core;
        installCore.Run(Arg.Any<LoadoutId>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<ResolvedInstall>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var outcomes = new List<InstallOutcome>();
                foreach (var item in call.Arg<IReadOnlyList<ResolvedInstall>>())
                {
                    var library = item.InLibrary ?? await item.Download(call.Arg<CancellationToken>());
                    outcomes.Add(new InstallOutcome(item.Name, library, Downloaded: item.InLibrary is null, LoadoutPackageOutcome.Installed, item.Dependency));
                }

                return (IReadOnlyList<InstallOutcome>)outcomes;
            });
        var services = new ServiceCollection().AddSingleton(installCore).BuildServiceProvider();
        var provider = new CompositeServiceProvider(services, serviceProvider);
        return new ModIoCatalog(rig.Api, rig.Connection, [rig.Game], provider, () => rig.Client, new ModIoPickups(provider, rig.Expected, NullLogger<ModIoPickups>.Instance), NullLogger<ModIoCatalog>.Instance);
    }

    private ModIoMod PayloadMod(ModIoRig rig, string name = "Wider Camera", bool clean = true)
    {
        var mod = TestMods.Mod(_modId, name, 1000, TestMods.File(_modId + 1, $"{_modId}.zip", _payload, clean: clean, binaryUrl: $"https://files.test/{_modId}/{_modId}.zip", modId: _modId));
        rig.Api.Mods_[(rig.Game.ModIoGameId, _modId)] = mod;
        return mod;
    }

    [Fact]
    public async Task SearchListsTheCardsOfTheGamesMods()
    {
        var rig = new ModIoRig(connection);
        var catalog = CatalogOf(rig, out _);
        rig.Api.Listings[$"mods {rig.Game.ModIoGameId} [camera] -downloads_total 30"] = new ModIoPage<ModIoMod>([TestMods.Mod(11, "Wider Camera", 1710000500, TestMods.File(12, "a.zip", version: "1.2.0"), downloads: 99)], 61, 30);

        var page = await catalog.Search(rig.Game, Query(catalog, " camera ", "-downloads_total", cursor: "30"), CancellationToken.None);

        var card = page.Cards.Should().ContainSingle().Subject;
        card.Id.Should().Be("11");
        card.Title.Should().Be("Wider Camera");
        card.Author.Should().Be("Author");
        card.Summary.Should().Be("A summary.");
        card.Version.Should().Be("1.2.0");
        card.Downloads.Should().Be(99);
        card.Page.Should().Be(new Uri("https://mod.io/g/test/m/wider-camera"));
        card.Icon.Should().Be(new Uri("https://thumb.modcdn.io/11.png"));
        card.Updated.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1710000500));
        card.Handle.Should().Be(new ModIoHandle(11));
        page.Total.Should().Be(61);
        page.NextCursor.Should().Be("60");
    }

    [Fact]
    public async Task TheLastPageHasNoCursor()
    {
        var rig = new ModIoRig(connection);
        var catalog = CatalogOf(rig, out _);
        rig.Api.Listings[$"mods {rig.Game.ModIoGameId} [] {catalog.Sorts[0].Key} 30"] = new ModIoPage<ModIoMod>([TestMods.Mod(11, "A", 1, null)], 31, 30);

        var page = await catalog.Search(rig.Game, Query(catalog, cursor: "30"), CancellationToken.None);

        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task AdultModsAreLeftOutUnlessAsked()
    {
        var rig = new ModIoRig(connection);
        var catalog = CatalogOf(rig, out _);
        rig.Api.Listings[$"mods {rig.Game.ModIoGameId} [] {catalog.Sorts[0].Key} 0"] = new ModIoPage<ModIoMod>([TestMods.Mod(1, "Tame", 1, null), TestMods.Mod(2, "Spicy", 1, null, adult: true)], 2, 0);

        var hidden = await catalog.Search(rig.Game, Query(catalog), CancellationToken.None);
        var shown = await catalog.Search(rig.Game, Query(catalog, adult: true), CancellationToken.None);

        hidden.Cards.Select(card => card.Title).Should().Equal("Tame");
        shown.Cards.Select(card => (card.Title, card.IsAdult)).Should().Equal(("Tame", false), ("Spicy", true));
    }

    [Fact]
    public async Task OnlyAGameModIoListsIsServedAndTheLabelFollowsTheGamesSync()
    {
        var download = new ModIoRig(connection);
        var subscribe = new ModIoRig(connection, syncs: true);
        var catalog = CatalogOf(download, out _);

        catalog.Serves(download.Game).Should().BeTrue();
        catalog.Serves(new FakeGame(GameId.From("Tests.Elsewhere"))).Should().BeFalse();
        catalog.InstallLabel(download.Game).Should().Be("Install");
        catalog.InstallLabel(subscribe.Game).Should().Be("Subscribe");
        catalog.DisplayName.Should().Be("mod.io");
    }

    [Fact]
    public async Task FindReturnsTheCardOfAModOfTheGameAndNothingForAnUnknownOne()
    {
        var rig = new ModIoRig(connection);
        var catalog = CatalogOf(rig, out _);
        PayloadMod(rig);

        (await catalog.Find(rig.Game, _modId.ToString(), CancellationToken.None))!.Title.Should().Be("Wider Camera");
        (await catalog.Find(rig.Game, "424242", CancellationToken.None)).Should().BeNull();
        (await catalog.Find(rig.Game, "not a number", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SearchingWithoutATokenAsksForOneInsteadOfListingNothing()
    {
        var rig = new ModIoRig(connection);
        rig.Api.Failure = new ModIoNotSignedInException();
        var catalog = CatalogOf(rig, out _);

        var act = async () => await catalog.Search(rig.Game, Query(catalog), CancellationToken.None);

        await act.Should().ThrowAsync<ModIoNotSignedInException>();
    }

    [Fact]
    public async Task Bg3InstallSubscribesWithDependenciesAndReportsTheInGameSignInNote()
    {
        var rig = new ModIoRig(connection, syncs: true);
        var catalog = CatalogOf(rig, out var core);
        var loadout = await rig.NewLoadout();
        PayloadMod(rig);
        var card = (await catalog.Find(rig.Game, _modId.ToString(), CancellationToken.None))!;

        var outcome = await catalog.Install(card, loadout, CancellationToken.None);

        rig.Api.Requests.Should().Contain($"subscribe {rig.Game.ModIoGameId} {_modId} deps=True");
        outcome.Should().BeOfType<CatalogInstallOutcome.Subscribed>().Which.Note
            .Should().Be($"Sign in to mod.io in the game's Mod Manager with this account; the mod appears in the {rig.Game.DisplayName} mods group after the game syncs.");
        await core.DidNotReceiveWithAnyArgs().Run(default, default!, default!, default);
    }

    [Fact]
    public async Task ASubscribeWithAReadOnlyTokenIsRefusedWithThePlainReason()
    {
        var rig = new ModIoRig(connection, syncs: true);
        rig.Api.SubscribeFailure = ModIoRefusedException.ReadOnlyToken();
        var catalog = CatalogOf(rig, out _);
        var loadout = await rig.NewLoadout();
        PayloadMod(rig);
        var card = (await catalog.Find(rig.Game, _modId.ToString(), CancellationToken.None))!;

        var outcome = await catalog.Install(card, loadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Message.Should().Contain("write scope");
    }

    [Fact]
    public async Task ADownloadGameInstallsTheFileAfterCheckingItsMd5()
    {
        var rig = new ModIoRig(connection);
        var catalog = CatalogOf(rig, out _);
        var loadout = await rig.NewLoadout();
        PayloadMod(rig);
        rig.Bytes.Serve($"https://files.test/{_modId}/{_modId}.zip", _payload);
        var card = (await catalog.Find(rig.Game, _modId.ToString(), CancellationToken.None))!;

        var outcome = await catalog.Install(card, loadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Installed>();
        var saved = ModIoLibraryFile.All(connection.Db).Single(row => row.ModId == _modId && row.GameId == rig.Game.ModIoGameId);
        saved.FileId.Should().Be(_modId + 1);
        saved.AsLibraryFile().Md5.Value.ToString().Should().BeEquivalentTo(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(_payload)));
    }

    [Fact]
    public async Task ADownloadWhoseBytesDoNotMatchTheListedMd5IsRefusedAndAddsNothing()
    {
        var rig = new ModIoRig(connection);
        var catalog = CatalogOf(rig, out _);
        var loadout = await rig.NewLoadout();
        PayloadMod(rig);
        rig.Bytes.Serve($"https://files.test/{_modId}/{_modId}.zip", Guid.NewGuid().ToByteArray());
        var card = (await catalog.Find(rig.Game, _modId.ToString(), CancellationToken.None))!;

        var outcome = await catalog.Install(card, loadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Message.Should().Contain("MD5 mismatch");
        ModIoLibraryFile.All(connection.Db).Should().NotContain(row => row.ModId == _modId && row.GameId == rig.Game.ModIoGameId);
    }

    [Fact]
    public async Task AFileTheScanFlaggedIsRefusedWithThePageLink()
    {
        var rig = new ModIoRig(connection);
        var catalog = CatalogOf(rig, out _);
        var loadout = await rig.NewLoadout();
        PayloadMod(rig, clean: false);
        var card = (await catalog.Find(rig.Game, _modId.ToString(), CancellationToken.None))!;

        var outcome = await catalog.Install(card, loadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Message.Should().Contain("https://mod.io/g/test/m/wider-camera");
        rig.Bytes.Fetched.Should().BeEmpty();
    }

    [Fact]
    public async Task ARejectedBinaryUrlOpensTheWebsiteAndArmsThePickupForAUserAction()
    {
        var rig = new ModIoRig(connection);
        var catalog = CatalogOf(rig, out _);
        var loadout = await rig.NewLoadout();
        var mod = PayloadMod(rig);
        // Nothing is served at the address: mod.io answers an expired or refused binary url with a 4xx.
        var card = (await catalog.Find(rig.Game, _modId.ToString(), CancellationToken.None))!;

        var outcome = await catalog.Install(card, loadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.OpenedOnWebsite>().Which.Page.Should().Be(new Uri(mod.ProfileUrl!));
        var expected = rig.Expected.Current.Should().ContainSingle().Subject;
        expected.FileName.Should().Be($"{_modId}.zip");
        expected.Size.Should().Be(_payload.Length);
    }

    [Fact]
    public async Task APickedUpFileIsVerifiedAgainstTheListedMd5AndAddedToTheLibrary()
    {
        var rig = new ModIoRig(connection);
        var catalog = CatalogOf(rig, out _);
        var loadout = await rig.NewLoadout();
        PayloadMod(rig);
        await catalog.Install((await catalog.Find(rig.Game, _modId.ToString(), CancellationToken.None))!, loadout, CancellationToken.None);
        var expected = rig.Expected.Current.Single();
        var folder = Path.Combine(Path.GetTempPath(), "modio-pickup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, expected.FileName);
            await File.WriteAllBytesAsync(path, _payload);

            await expected.Sink.Accept(NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(path), (_, _) => -1, CancellationToken.None);

            ModIoLibraryFile.All(connection.Db).Should().ContainSingle(row => row.ModId == _modId && row.GameId == rig.Game.ModIoGameId);
            rig.Expected.Current.Should().BeEmpty("the expectation ends once the file arrived");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task APickedUpFileWithTheWrongBytesIsRefused()
    {
        var rig = new ModIoRig(connection);
        var catalog = CatalogOf(rig, out _);
        var loadout = await rig.NewLoadout();
        PayloadMod(rig);
        await catalog.Install((await catalog.Find(rig.Game, _modId.ToString(), CancellationToken.None))!, loadout, CancellationToken.None);
        var expected = rig.Expected.Current.Single();
        var folder = Path.Combine(Path.GetTempPath(), "modio-pickup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, expected.FileName);
            await File.WriteAllBytesAsync(path, Guid.NewGuid().ToByteArray());

            var act = async () => await expected.Sink.Accept(NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(path), (_, _) => -1, CancellationToken.None);

            await act.Should().ThrowAsync<ModIoIntegrityException>();
            ModIoLibraryFile.All(connection.Db).Should().NotContain(row => row.ModId == _modId && row.GameId == rig.Game.ModIoGameId);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task NoTokenIsARefusalNamingTheSettingsRow()
    {
        var rig = new ModIoRig(connection);
        var catalog = CatalogOf(rig, out _);
        var loadout = await rig.NewLoadout();
        var card = new CatalogCard(_modId.ToString(), "Mod", "", "", null, null, "", 0, null, false, null, new ModIoHandle(_modId));
        rig.Api.Failure = new ModIoNotSignedInException();

        var outcome = await catalog.Install(card, loadout, CancellationToken.None);

        outcome.Should().BeOfType<CatalogInstallOutcome.Refused>().Which.Why.Message.Should().Contain("Settings").And.Contain("mod.io");
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels().AddGameInstallMetadataModel().AddLoadoutAbstractions().AddOSInterop().AddFileExtractors();
        }
    }
}
