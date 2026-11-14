using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk;
using SilverModManager.Backend;
using SilverModManager.Library.Lock;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.Workshop;
using SilverModManager.Library.Workshop;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Library.Tests.Lock;

public class SteamWorkshopLockTests(IConnection connection)
{
    private static readonly LockEntry Entry = new("workshop-100", "2026-11-13", "100", "Long War", "", LockSources.SteamWorkshop);

    private LockExport Export(IWorkshopLockEntries? workshop)
        => new([], new LibraryFileDigests(Substitute.For<IFileStore>(), connection), new CollectionOwnership([]), workshop);

    [Fact]
    public async Task AWorkshopItemExportsWithItsIdAndNoHash()
    {
        using var rig = new WorkshopRig();
        rig.Library.Subscribe(100, folderModified: 1000);
        rig.Api.Add(100, "Long War", updated: 1_700_000_000);
        var loadout = await new LockFixtures(connection).CreateLoadout();
        var registry = Substitute.For<IGameRegistry>();
        var install = rig.Install;
        registry.TryGetGameInstallation(Arg.Any<Loadout.ReadOnly>(), out Arg.Any<GameInstallation?>()).Returns(call =>
        {
            call[1] = install;
            return true;
        });
        var export = Export(new WorkshopLockEntries(registry, rig.View));

        var lockFile = await export.FromLoadout(loadout, TestContext.Current.CancellationToken);

        var entry = lockFile.Entries.Should().ContainSingle().Subject;
        entry.Id.Should().Be("workshop-100");
        entry.FileId.Should().Be("100");
        entry.Asset.Should().Be("Long War");
        entry.Sha256.Should().BeEmpty();
        entry.Source.Should().Be("steam-workshop");
        LockFile.Parse(lockFile.Write()).Entries.Should().Equal(lockFile.Entries, "the entry survives the file it is written to");
    }

    [Fact]
    public async Task AGameWithoutWorkshopItemsAddsNoEntries()
    {
        var loadout = await new LockFixtures(connection).CreateLoadout();
        var registry = Substitute.For<IGameRegistry>();
        registry.TryGetGameInstallation(Arg.Any<Loadout.ReadOnly>(), out Arg.Any<GameInstallation?>()).Returns(false);
        using var rig = new WorkshopRig();

        var lockFile = await Export(new WorkshopLockEntries(registry, rig.View)).FromLoadout(loadout, TestContext.Current.CancellationToken);

        lockFile.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportListsEachWorkshopEntryAsASubscribeLinkInTheReport()
    {
        var import = new LockImport(connection, [], Substitute.For<SilverModManager.Abstractions.Library.ILibraryService>());

        var plan = await import.Plan(new LockFile([Entry]), game: null, TestContext.Current.CancellationToken);
        var summary = await import.Apply(plan, TestContext.Current.CancellationToken);

        summary.Failed.Should().BeEmpty("an item Steam subscribes to is not a failed download");
        var outcome = summary.Subscribe.Should().ContainSingle().Subject;
        outcome.Entry.Should().Be(Entry);
        outcome.Message.Should().Contain("Long War").And.Contain("steam://url/CommunityFilePage/100").And.Contain("https://steamcommunity.com/sharedfiles/filedetails/?id=100");
        outcome.File.Should().BeNull();
    }

    [Fact]
    public async Task VerifyWarnsThatAWorkshopEntryIsSubscribedToInSteamAndFailsNothing()
    {
        var verify = new LockVerify(connection, new LibraryFileDigests(Substitute.For<IFileStore>(), connection));

        var report = await verify.Verify(new LockFile([Entry]), TestContext.Current.CancellationToken);

        var item = report.Items.Should().ContainSingle().Subject;
        item.Level.Should().Be(LockLevel.Warn);
        item.Message.Should().Contain("Steam").And.Contain("steam://url/CommunityFilePage/100");
        report.ExitCode.Should().Be(0);
    }

    [Fact]
    public void ALockMayNameTheWorkshopAsASource()
    {
        LockSources.All.Should().Contain("steam-workshop");
    }

    [Fact]
    public void AWorkshopEntryWithoutAFileIdIsNotMadeUpOfAnId()
    {
        var entry = new LockEntry("x", "", null, "Thing", "", LockSources.SteamWorkshop);

        var act = () => WorkshopLockEntries.SubscribeMessage(entry);

        act.Should().Throw<LockException>().Which.Message.Should().Contain("no Steam Workshop item id");
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
