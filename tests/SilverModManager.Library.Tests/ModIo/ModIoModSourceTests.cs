using DynamicData.Kernel;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Games;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Backend;
using SilverModManager.Library.Listing;
using SilverModManager.Library.ModIo;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.GitHub;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.Library.Tests.ModSources;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.ModIo;

internal static class ModIoModSources
{
    public static ModIoModSource Create(ModIoRig rig, IServiceProvider provider, ILibraryService? library = null, IOSInterop? os = null)
    {
        var services = new ServiceCollection().AddSingleton(library ?? Substitute.For<ILibraryService>()).BuildServiceProvider();
        var composite = new CompositeServiceProvider(services, provider);
        return new ModIoModSource(rig.Mods, rig.Connection, composite, () => rig.Client, new ModIoPickups(composite, rig.Expected, NullLogger<ModIoPickups>.Instance), os ?? Substitute.For<IOSInterop>(), NullLogger<ModIoModSource>.Instance);
    }

    public static async Task<LibraryItem.ReadOnly> AddItem(IConnection connection, uint gameId, uint modId, uint fileId, long dateUpdated, string? name = null, string version = "1.0")
    {
        var content = "content " + Guid.NewGuid();
        var file = await new LockFixtures(connection).AddModIoFile(name ?? $"mod-{fileId}.zip", content, LockFixtures.DigestOf(content), gameId, modId, fileId, version, dateUpdated);
        return file.AsLibraryItem();
    }
}

public class ModIoModSourceTests(IServiceProvider serviceProvider, IConnection connection)
{
    private readonly uint _modId = (uint)Random.Shared.Next(1_000_000, 900_000_000);

    private static Optional<ModUpdate> LatestOf(IObservable<Optional<ModUpdate>> observed)
    {
        var seen = Optional<ModUpdate>.None;
        using (observed.Subscribe(value => seen = value)) { }
        return seen;
    }

    private ModIoRig Arrange(long updated, uint modfileId, string version = "2.0")
    {
        var rig = new ModIoRig(connection);
        rig.Api.Mods_[(rig.Game.ModIoGameId, _modId)] = TestMods.Mod(_modId, "Mod", updated, TestMods.File(modfileId, $"mod-{modfileId}.zip", version: version, added: updated, modId: _modId));
        return rig;
    }

    [Fact]
    public async Task ANewModfileIsAnUpdate()
    {
        var rig = Arrange(updated: 2000, modfileId: 22, version: "2.0");
        var item = await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, _modId, 21, dateUpdated: 1000, version: "1.0");
        var source = ModIoModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        var update = LatestOf(source.ObserveUpdate(item)).Value;
        update.CurrentVersion.Should().Be("1.0");
        update.NewestVersion.Should().Be("2.0");
        update.PublishedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(2000));
        update.Count.Should().Be(1);
        source.Owns(update).Should().BeTrue();
        source.Owns(item).Should().BeTrue();
        source.AllUpdates(rig.Game).Should().ContainSingle();
        bool? any = null;
        using (source.ObserveHasAnyUpdates(rig.Game).Subscribe(value => any = value)) { }
        any.Should().BeTrue();
    }

    [Fact]
    public async Task ADescriptionEditThatMovedTheDateButNotTheModfileIsNoUpdate()
    {
        var rig = Arrange(updated: 9999, modfileId: 21);
        var item = await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, _modId, 21, dateUpdated: 1000);
        var source = ModIoModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeFalse();
        source.AllUpdates(rig.Game).Should().BeEmpty();
    }

    [Fact]
    public async Task ANewModfileWithoutANewerDateIsNoUpdate()
    {
        var rig = Arrange(updated: 1000, modfileId: 22);
        var item = await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, _modId, 21, dateUpdated: 1000);
        var source = ModIoModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task AModfileTheLibraryAlreadyHoldsIsNoUpdate()
    {
        var rig = Arrange(updated: 2000, modfileId: 22);
        await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, _modId, 22, dateUpdated: 500);
        var item = await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, _modId, 21, dateUpdated: 1000);
        var source = ModIoModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task AFileOfAnotherGameIsNotCheckedForThisOne()
    {
        var rig = Arrange(updated: 2000, modfileId: 22);
        await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId + 1, _modId, 21, dateUpdated: 1000);
        var source = ModIoModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        rig.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckUpdatesWithoutForceWithinTheHourMakesNoRequest()
    {
        var rig = Arrange(updated: 1000, modfileId: 21);
        await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, _modId, 21, dateUpdated: 1000);
        var source = ModIoModSources.Create(rig, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromMinutes(30));
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        rig.Api.Requests.Should().Equal($"mod {rig.Game.ModIoGameId} {_modId}");

        await source.CheckUpdates(rig.Game, force: true, CancellationToken.None);
        rig.Api.Requests.Should().HaveCount(2, "Refresh asks again");
    }

    [Fact]
    public async Task ARateLimitOrAMissingTokenLeavesTheCheckQuietAndKeepsWhatWasKnown()
    {
        var rig = Arrange(updated: 2000, modfileId: 22);
        var item = await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, _modId, 21, dateUpdated: 1000);
        var source = ModIoModSources.Create(rig, serviceProvider);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        rig.Api.Failure = new ModIoRateLimitedException(TimeSpan.FromMinutes(5));

        await source.CheckUpdates(rig.Game, force: true, CancellationToken.None);
        rig.Api.Failure = new ModIoNotSignedInException();
        await source.CheckUpdates(rig.Game, force: true, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeTrue("the earlier answer still stands");
    }

    [Fact]
    public async Task AFrozenItemHasNoUpdate()
    {
        var rig = Arrange(updated: 2000, modfileId: 22);
        var item = await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, _modId, 21, dateUpdated: 1000);
        var source = ModIoModSources.Create(rig, serviceProvider);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeTrue();

        await source.SetUpdatesHidden([item], hidden: true, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task ThePageLinkIsTheModsProfile()
    {
        var rig = Arrange(updated: 2000, modfileId: 22);
        var item = await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, _modId, 21, dateUpdated: 1000);
        var source = ModIoModSources.Create(rig, serviceProvider);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        source.PageUri(item).Should().Be(new Uri("https://mod.io/g/test/m/mod"));
        source.DisplayName.Should().Be("mod.io");
    }

    [Fact]
    public async Task AnUpdateThatTheSiteRejectsForTheBinaryUrlOpensNothingForUpdateAllAndArmsNoPickup()
    {
        var rig = Arrange(updated: 2000, modfileId: 22);
        var item = await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, _modId, 21, dateUpdated: 1000);
        var os = Substitute.For<IOSInterop>();
        var source = ModIoModSources.Create(rig, serviceProvider, os: os);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([source.AllUpdates(rig.Game).Single()], Replace: true, MayOpenBrowser: false), CancellationToken.None);

        outcome.AccountCannotDownload.Should().BeTrue();
        outcome.OpenedInBrowser.Should().BeEmpty();
        os.DidNotReceiveWithAnyArgs().OpenUri(default!);
        rig.Expected.Current.Should().BeEmpty();
        LibraryItem.Load(connection.Db, item.Id).IsValid().Should().BeTrue();
    }

    [Fact]
    public async Task ASingleUserStartedUpdateThatTheSiteRejectsOpensThePageAndArmsThePickup()
    {
        var rig = Arrange(updated: 2000, modfileId: 22);
        await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, _modId, 21, dateUpdated: 1000);
        var os = Substitute.For<IOSInterop>();
        var source = ModIoModSources.Create(rig, serviceProvider, os: os);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([source.AllUpdates(rig.Game).Single()], Replace: false, MayOpenBrowser: true), CancellationToken.None);

        outcome.OpenedInBrowser.Should().Equal(new Uri("https://mod.io/g/test/m/mod"));
        os.Received(1).OpenUri(new Uri("https://mod.io/g/test/m/mod"));
        rig.Expected.Current.Should().ContainSingle().Which.FileName.Should().Be("mod-22.zip");
    }

    [Fact]
    public async Task ApplyUpdates_Replace_SwapsWithTheModUpdateOptions()
    {
        var payload = Guid.NewGuid().ToByteArray().Concat(new byte[300]).ToArray();
        var rig = new ModIoRig(connection);
        rig.Api.Mods_[(rig.Game.ModIoGameId, _modId)] = TestMods.Mod(_modId, "Mod", 2000, TestMods.File(_modId + 2, $"swap-{_modId}.zip", payload, binaryUrl: $"https://files.test/{_modId}.zip", modId: _modId));
        rig.Bytes.Serve($"https://files.test/{_modId}.zip", payload);
        var item = await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, _modId, _modId + 1, dateUpdated: 1000);
        var library = Substitute.For<ILibraryService>();
        library.ReplaceLinkedItemsInAllLoadouts(default, default, default).ReturnsForAnyArgs(ValueTask.FromResult(LibraryItemReplacementResult.Success));
        var source = ModIoModSources.Create(rig, serviceProvider, library);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([source.AllUpdates(rig.Game).Single()], Replace: true, MayOpenBrowser: true), CancellationToken.None);

        outcome.DownloadFailures.Should().BeEmpty();
        var downloaded = outcome.Downloaded.Should().ContainSingle().Subject;
        await library.Received(1).ReplaceLinkedItemsInAllLoadouts(
            Arg.Is<LibraryItem.ReadOnly>(old => old.Id == item.Id), Arg.Is<LibraryItem.ReadOnly>(next => next.Id == downloaded.Id),
            Arg.Is<ReplaceLibraryItemOptions>(options => options == ReplaceLibraryItemOptions.ModUpdate));
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

public class ModIoUpdateAllBrowserPolicyTests(IServiceProvider serviceProvider, IConnection connection) : ModSourceBrowserPolicyContract(connection)
{
    protected override async Task<(IModSource Source, ModUpdate Update, Func<IReadOnlyList<Uri>> OpenedByOs)> Arrange()
    {
        var modId = (uint)Random.Shared.Next(1_000_000, 900_000_000);
        var payload = Guid.NewGuid().ToByteArray().Concat(new byte[300]).ToArray();
        var rig = new ModIoRig(connection);
        rig.Api.Mods_[(rig.Game.ModIoGameId, modId)] = TestMods.Mod(modId, "Policy", 2000, TestMods.File(modId + 2, $"p2-{modId}.zip", payload, binaryUrl: $"https://files.test/{modId}.zip", modId: modId));
        rig.Bytes.Serve($"https://files.test/{modId}.zip", payload);
        await ModIoModSources.AddItem(connection, rig.Game.ModIoGameId, modId, modId + 1, dateUpdated: 1000);
        var library = Substitute.For<ILibraryService>();
        library.ReplaceLinkedItemsInAllLoadouts(default, default, default).ReturnsForAnyArgs(ValueTask.FromResult(LibraryItemReplacementResult.Success));
        var os = Substitute.For<IOSInterop>();
        var opened = new List<Uri>();
        os.When(x => x.OpenUri(Arg.Any<Uri>())).Do(call => opened.Add(call.Arg<Uri>()));
        var source = ModIoModSources.Create(rig, serviceProvider, library, os);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        return (source, source.AllUpdates(rig.Game).Single(), () => opened);
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

public class ModIoKindTests(IConnection connection)
{
    private static readonly ModIoKind Kind = new();

    [Fact]
    public async Task TheSourceColumnSaysModIoWithTheListedVersion()
    {
        var item = await ModIoModSources.AddItem(connection, 6715, 7, 70, dateUpdated: 1000, version: "3.1");

        var match = Kind.Match(LibraryItem.Load(connection.Db, item.Id))!;

        match.Kind.Should().Be(LibraryItemKinds.Other);
        match.Version.Should().Be("3.1");
        match.Host.Should().Be("mod.io");
    }

    [Fact]
    public async Task AnItemFromElsewhereDoesNotMatch()
    {
        var other = (await new LockFixtures(connection).AddLibraryFile("local.zip", "x" + Guid.NewGuid(), null)).AsLibraryItem();

        Kind.Match(other).Should().BeNull();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels();
        }
    }
}
