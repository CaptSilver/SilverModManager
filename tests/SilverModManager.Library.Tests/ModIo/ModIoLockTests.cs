using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Backend;
using SilverModManager.Library.Lock;
using SilverModManager.Library.ModIo;
using SilverModManager.Library.Sources;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.GitHub;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.Library.Tests.Workshop;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Library.Tests.ModIo;

public class ModIoLibrarySourceTests(IServiceProvider serviceProvider, IConnection connection)
{
    private readonly LockFixtures _fixtures = new(connection);
    private readonly byte[] _payload = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 3000).Select(i => (byte)(i * 11 + 1))).ToArray();
    private readonly uint _modId = (uint)Random.Shared.Next(1_000_000, 900_000_000);

    private Sha256Value PayloadSha => Sha256Value.From(SHA256.HashData(_payload));

    private ModIoLibrarySource SourceOver(ModIoRig rig)
    {
        var services = new ServiceCollection().AddSingleton<IModIoApi>(rig.Api).AddSingleton(rig.Client).BuildServiceProvider();
        return new ModIoLibrarySource(new CompositeServiceProvider(services, serviceProvider), new LibraryFileDigests(Substitute.For<IFileStore>(), connection));
    }

    private LibrarySourceContext ContextOf(ModIoRig rig) => new(connection, rig.Game, null);

    private LibrarySourceEntry EntryFor(string asset, uint fileId) => new("pinned", "1.0", $"{_modId}:{fileId}", asset, PayloadSha.ToString(), LockSources.ModIo);

    [Fact]
    public async Task ALockEntryRoundTripsWithTheModAndFileIds()
    {
        var rig = new ModIoRig(connection);
        var loadout = await _fixtures.CreateLoadout();
        var file = await _fixtures.AddModIoFile($"io-{_modId}.zip", "io " + _modId, LockFixtures.DigestOf("io " + _modId), rig.Game.ModIoGameId, _modId, 77, "1.2");
        await _fixtures.Link(loadout, file, "Io mod");
        var digests = new LibraryFileDigests(Substitute.For<IFileStore>(), connection);
        var export = new LockExport([SourceOver(rig)], digests, new CollectionOwnership([]));

        var written = (await export.FromLoadout(loadout, TestContext.Current.CancellationToken)).Write();
        var entry = LockFile.Parse(written).Entries.Should().ContainSingle().Subject;

        entry.Should().Be(new LockEntry("io-mod", "1.2", $"{_modId}:77", $"io-{_modId}.zip", LockFixtures.DigestOf("io " + _modId).ToString(), "modio"));
    }

    [Fact]
    public async Task ARowWithoutAnyDigestCannotBePinned()
    {
        var rig = new ModIoRig(connection);
        var file = await _fixtures.AddModIoFile($"nodigest-{_modId}.zip", "x", pinned: null, rig.Game.ModIoGameId, _modId, 1);

        var act = async () => await SourceOver(rig).Describe("g", file.AsLibraryItem(), file, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<LockException>();
    }

    [Fact]
    public async Task AnItemFromAnotherSourceIsNotDescribed()
    {
        var rig = new ModIoRig(connection);
        var file = await _fixtures.AddLibraryFile("local.zip", "x" + _modId, LockFixtures.DigestOf("x" + _modId));

        (await SourceOver(rig).Describe("g", file.AsLibraryItem(), file, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task DownloadAsksForTheCurrentFileAddressAndVerifiesTheDigest()
    {
        var rig = new ModIoRig(connection);
        var asset = $"restored-{_modId}.zip";
        rig.Api.FilesOf[(rig.Game.ModIoGameId, _modId)] = [TestMods.File(77, asset, _payload, binaryUrl: $"https://files.test/{_modId}/fresh.zip", modId: _modId)];
        rig.Bytes.Serve($"https://files.test/{_modId}/fresh.zip", _payload);

        var file = await SourceOver(rig).Download(EntryFor(asset, 77), PayloadSha, ContextOf(rig), TestContext.Current.CancellationToken);

        rig.Api.Requests.Should().Equal($"files {rig.Game.ModIoGameId} {_modId}");
        file.Sha256.Value.Should().Be(PayloadSha);
        var model = ModIoLibraryFile.Load(connection.Db, file.Id);
        (model.GameId, model.ModId, model.FileId, model.Version).Should().Be((rig.Game.ModIoGameId, _modId, 77u, "1.0"));
    }

    [Fact]
    public async Task AModfileTheModNoLongerListsCannotBeRestored()
    {
        var rig = new ModIoRig(connection);
        rig.Api.FilesOf[(rig.Game.ModIoGameId, _modId)] = [TestMods.File(78, "other.zip", _payload)];

        var act = async () => await SourceOver(rig).Download(EntryFor("gone.zip", 77), PayloadSha, ContextOf(rig), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<LockException>().WithMessage("*no longer lists*");
    }

    [Fact]
    public async Task ADigestThatDoesNotMatchLeavesNothingInTheLibrary()
    {
        var rig = new ModIoRig(connection);
        var asset = $"mismatch-{_modId}.zip";
        rig.Api.FilesOf[(rig.Game.ModIoGameId, _modId)] = [TestMods.File(77, asset, _payload, binaryUrl: $"https://files.test/{_modId}/m.zip")];
        rig.Bytes.Serve($"https://files.test/{_modId}/m.zip", _payload);
        var wrong = Sha256Value.From(SHA256.HashData("other"u8));

        var act = async () => await SourceOver(rig).Download(EntryFor(asset, 77) with { Sha256 = wrong.ToString() }, wrong, ContextOf(rig), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ModIoIntegrityException>();
        LibraryFile.All(connection.Db).Should().NotContain(file => file.FileName == asset);
    }

    [Fact]
    public void AnEntryWithoutBothIdsOrForAnotherGameCannotBeDownloaded()
    {
        var rig = new ModIoRig(connection);
        var source = SourceOver(rig);

        source.WhyNotDownloadable(EntryFor("a.zip", 1) with { FileId = null }, ContextOf(rig)).Should().Contain("mod and file id");
        source.WhyNotDownloadable(EntryFor("a.zip", 1) with { FileId = "12" }, ContextOf(rig)).Should().Contain("mod and file id");
        source.WhyNotDownloadable(EntryFor("a.zip", 1), new LibrarySourceContext(connection, null, null)).Should().Contain("mod.io");
        source.WhyNotDownloadable(EntryFor("a.zip", 1), ContextOf(rig)).Should().BeNull();
    }

    [Fact]
    public void ModIoIsASourceALockMayName()
    {
        LockSources.All.Should().Contain("modio");
        SourceOver(new ModIoRig(connection)).Name.Should().Be("modio");
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

/// <summary>
/// A game whose mods the account is subscribed to carries no library file, so the lock names the subscription: the mod
/// id and no hash, the way a Steam Workshop item is named.
/// </summary>
public class ModIoLockEntriesTests(IConnection connection)
{
    private static readonly LockEntry Entry = new("modio-4101", "1.2.0", "4101", "Wider Camera", "", LockSources.ModIo);

    private static ModIoLockEntries Entries(ModIoRig rig, bool installed = true)
    {
        var registry = Substitute.For<IGameRegistry>();
        var install = new SteamLibraryFixture().Install(rig.Game);
        registry.TryGetGameInstallation(Arg.Any<Loadout.ReadOnly>(), out Arg.Any<GameInstallation?>()).Returns(call =>
        {
            call[1] = installed ? install : null;
            return installed;
        });
        return new ModIoLockEntries(registry, rig.Api);
    }

    private LockExport Export(IModIoLockEntries entries)
        => new([], new LibraryFileDigests(Substitute.For<IFileStore>(), connection), new CollectionOwnership([]), workshop: null, modIo: entries);

    [Fact]
    public async Task ASubscribeOnlyGamesSubscriptionsExportByModIdWithoutAHash()
    {
        var rig = new ModIoRig(connection, syncs: true);
        rig.Api.SubscribedMods.Add(TestMods.Mod(4101, "Wider Camera", 1, TestMods.File(1, "a.zip", version: "1.2.0")));
        var loadout = await new LockFixtures(connection).CreateLoadout();

        var lockFile = await Export(Entries(rig)).FromLoadout(loadout, TestContext.Current.CancellationToken);

        lockFile.Entries.Should().ContainSingle().Which.Should().Be(Entry);
        LockFile.Parse(lockFile.Write()).Entries.Should().Equal(lockFile.Entries);
        rig.Api.Requests.Should().Equal($"subscribed {rig.Game.ModIoGameId}");
    }

    [Fact]
    public async Task ADownloadGameAsksModIoNothing()
    {
        var rig = new ModIoRig(connection);
        var loadout = await new LockFixtures(connection).CreateLoadout();

        var lockFile = await Export(Entries(rig)).FromLoadout(loadout, TestContext.Current.CancellationToken);

        lockFile.Entries.Should().BeEmpty();
        rig.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutATokenThereAreNoSubscriptionsToExport()
    {
        var rig = new ModIoRig(connection, syncs: true);
        rig.Api.Failure = new ModIoNotSignedInException();
        var loadout = await new LockFixtures(connection).CreateLoadout();

        var lockFile = await Export(Entries(rig)).FromLoadout(loadout, TestContext.Current.CancellationToken);

        lockFile.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAnswerThatNeverCameFailsTheExportInsteadOfLeavingTheSubscriptionsOut()
    {
        var rig = new ModIoRig(connection, syncs: true);
        rig.Api.Failure = new ModIoUnavailableException("The app could not reach mod.io.");
        var loadout = await new LockFixtures(connection).CreateLoadout();

        var act = async () => await Export(Entries(rig)).FromLoadout(loadout, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<LockException>().WithMessage("*mod.io*");
    }

    [Fact]
    public async Task ImportListsEachSubscriptionWithTheVerbThatSubscribes()
    {
        var import = new LockImport(connection, [], Substitute.For<ILibraryService>());

        var plan = await import.Plan(new LockFile([Entry]), game: null, TestContext.Current.CancellationToken);
        var summary = await import.Apply(plan, TestContext.Current.CancellationToken);

        summary.Failed.Should().BeEmpty("a subscription is not a failed download");
        var outcome = summary.Subscribe.Should().ContainSingle().Subject;
        outcome.Message.Should().Contain("Wider Camera").And.Contain("modio install").And.Contain("4101");
    }

    [Fact]
    public async Task VerifyWarnsThatASubscriptionIsNotCheckedAndFailsNothing()
    {
        var verify = new LockVerify(connection, new LibraryFileDigests(Substitute.For<IFileStore>(), connection));

        var report = await verify.Verify(new LockFile([Entry]), TestContext.Current.CancellationToken);

        var item = report.Items.Should().ContainSingle().Subject;
        item.Level.Should().Be(LockLevel.Warn);
        item.Message.Should().Contain("mod.io");
        report.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task AModIoEntryWithAHashStillTakesTheDownloadPath()
    {
        var pinned = Entry with { Sha256 = "not a digest" };
        var import = new LockImport(connection, [], Substitute.For<ILibraryService>());

        var plan = await import.Plan(new LockFile([pinned]), game: null, TestContext.Current.CancellationToken);

        plan.Items.Single().Action.Should().NotBe(LockAction.Subscribe);
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
