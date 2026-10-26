using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Games.Larian.BaldursGate3.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Larian.BaldursGate3;
using SilverModManager.Games.Larian.BaldursGate3.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3.Modsettings;

/// <summary>
/// What an Apply writes to <c>modsettings.lsx</c>: the game's own modules and version kept as they were, then one
/// entry for each pak that should load, in the pak order.
/// </summary>
public class ModsettingsFileWriteTests(ITestOutputHelper helper) : ModsettingsFixture(helper)
{
    private const string ThirdPartyUuid = "d4d4d4d4-0000-4000-8000-000000000004";
    private const string PlainTwoUuid = "d5d5d5d5-0000-4000-8000-000000000005";

    [Fact]
    public async Task BaseEntriesAndTheVersionAreKeptAsTheyWere()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await WriteModsettings(
            [.. BaseEntries, Entry("ThirdParty", "ThirdParty", "0", ThirdPartyUuid, "7")],
            """<version major="4" minor="9" revision="1" build="77"/>""");

        await Apply(loadout);

        await Verify(DiskText());
    }

    [Fact]
    public async Task TheFileIsWrittenInThePlayerProfilesFolderTheGameReports()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await WriteModsettings(BaseEntries);

        await Apply(loadout);

        var reported = GameInstallation.Game.GetLocations(GameInstallation.LocatorResult.Path.FileSystem, GameInstallation.LocatorResult);
        reported[Bg3Constants.PlayerProfilesLocationId].Combine("modsettings.lsx").FileExists.Should().BeTrue();
        DiskNames().Should().Contain("Alpha");
    }

    [Fact]
    public async Task APakMissingBecauseTheModsFolderMovedIsDeployedAgain()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await WriteModsettings(BaseEntries);
        await Apply(loadout);
        OnDisk(Pak("alpha.pak")).FileExists.Should().BeTrue();

        // What the first Apply after the game moves from its Proton prefix to the native folders sees: the
        // paks of the last Apply are not where they were left.
        OnDisk(Pak("alpha.pak")).Delete();
        await Apply(loadout);

        OnDisk(Pak("alpha.pak")).FileExists.Should().BeTrue();
        DiskNames().Should().Contain("Alpha");
    }

    [Fact]
    public async Task APakTheUserDeletedStaysDeletedWhileOtherPaksAreStillThere()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await InstallMod(loadout, "Beta mod", "beta.pak", BetaPak());
        await WriteModsettings(BaseEntries);
        await Apply(loadout);

        OnDisk(Pak("alpha.pak")).Delete();
        await Apply(loadout);

        OnDisk(Pak("alpha.pak")).FileExists.Should().BeFalse("only the Mods folder as a whole counts as moved");
        OnDisk(Pak("beta.pak")).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task ActivePaksAreWrittenInTheSortOrder()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await InstallMod(loadout, "Beta mod", "beta.pak", BetaPak());
        await WriteModsettings(BaseEntries);
        await Apply(loadout);
        DiskNames().Should().Equal("GustavX", "HonourX", "Alpha", "Beta");

        using (var tx = Connection.BeginTransaction())
        {
            Variety.StageOrder(SortOrderOf(loadout), ["beta.pak", "alpha.pak"], tx);
            await tx.Commit();
        }
        await Apply(loadout);

        DiskNames().Should().Equal("GustavX", "HonourX", "Beta", "Alpha");
    }

    [Fact]
    public async Task SwitchedOffPaksAndDisabledModsAreLeftOut()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        var beta = await InstallMod(loadout, "Beta mod", "beta.pak", BetaPak());
        await WriteModsettings(BaseEntries);
        await Apply(loadout);

        await DisableItem(beta.Value);
        await Apply(loadout);
        DiskNames().Should().Equal("GustavX", "HonourX", "Alpha");

        await EnableItem(beta.Value);
        await Variety.SetEnabled(SortOrderOf(loadout), new SortItemKey<string>("alpha.pak"), false);
        await Apply(loadout);
        DiskNames().Should().Equal("GustavX", "HonourX", "Beta");
    }

    [Fact]
    public async Task SwitchingTheGameInstalledGroupOffOmitsItsPaks()
    {
        await WriteGameInstalledPak();
        var loadout = await CreateLoadout();
        await Apply(loadout);
        DiskNames().Should().Equal("GustavX", "HonourX", "Game Mod");

        await DisableItem(GameInstalledGroupOf(loadout).Id);
        await Apply(loadout);
        DiskNames().Should().Equal("GustavX", "HonourX");
    }

    [Fact]
    public async Task AGameInstalledPakWhoseRowSwitchIsOffIsWrittenWhileTheGroupIsOn()
    {
        await WriteGameInstalledPak();
        var loadout = await CreateLoadout();
        await Apply(loadout);

        var row = Variety.StoredOrder(Refresh(loadout)).Single(r => r.Name == GameModPak).StoredRowId;
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(row, SilverModManager.Games.Larian.BaldursGate3.Models.Bg3PakSortOrderItem.IsEnabled, false);
            await tx.Commit();
        }
        await Apply(loadout);

        DiskNames().Should().Equal("GustavX", "HonourX", "Game Mod");
    }

    [Fact]
    public async Task PaksWithoutARowYetAreAppendedNotOmitted()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Beta mod", "beta.pak", BetaPak());
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await WriteModsettings(BaseEntries);

        await Apply(loadout);

        // The sort order manager reconciles in the background as the mods land, so whether the two paks got their
        // rows together (name order) or one after the other (install order) depends on timing. Which pak goes
        // first is covered by the next test, which runs the reconcile between the installs.
        var names = DiskNames();
        names.Take(2).Should().Equal("GustavX", "HonourX");
        names.Skip(2).Should().BeEquivalentTo("Alpha", "Beta");
    }

    [Fact]
    public async Task APakThatArrivesAfterTheOrderWasStoredGoesAfterThePaksAlreadyInIt()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Beta mod", "beta.pak", BetaPak());
        await Game.SortOrderManager.UpdateLoadOrders(loadout.LoadoutId);
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await WriteModsettings(BaseEntries);

        await Apply(loadout);

        DiskNames().Should().Equal("GustavX", "HonourX", "Beta", "Alpha");
    }

    [Fact]
    public async Task AnExternalChangesPakIsWritten()
    {
        var loadout = await CreateLoadout();
        await WritePak("plain.pak", SyntheticPak.ForModule("Plain", "Plain", ThirdPartyUuid, SyntheticPak.Version(1)));
        await WriteModsettings(BaseEntries);
        await Apply(loadout);
        await Apply(loadout);

        DiskNames().Should().Equal("GustavX", "HonourX", "Plain");
    }

    [Fact]
    public async Task ADuplicateModuleIsWrittenOnceFromTheAppsCopy()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await WritePak("alpha-ab12.pak", SyntheticPak.ForPublishedModule("Alpha", "Alpha downloaded", AlphaUuid, SyntheticPak.Version(1), "4242"));
        await WriteModsettings([.. BaseEntries, Entry("Alpha", "Alpha downloaded", "4242", AlphaUuid)]);

        await Apply(loadout);

        var written = Disk().Entries.Where(e => e.Uuid == AlphaUuid).ToArray();
        written.Should().ContainSingle().Which.Name.Should().Be("Alpha");
        written[0].PublishHandle.Should().Be("0", "the copy the app installed is the one written");
    }

    [Fact]
    public async Task ADuplicateModuleIsReportedWithTheCopyThatWasKept()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await WritePak("alpha-ab12.pak", SyntheticPak.ForPublishedModule("Alpha", "Alpha downloaded", AlphaUuid, SyntheticPak.Version(1), "4242"));
        await WriteModsettings([.. BaseEntries, Entry("Alpha", "Alpha downloaded", "4242", AlphaUuid)]);
        await Apply(loadout);

        var emitter = Game.DiagnosticEmitters.OfType<DuplicateModuleEmitter>().Single();
        var diagnostic = (await emitter.Diagnose(Refresh(loadout), CancellationToken.None).ToListAsync()).Should().ContainSingle().Subject;

        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.Larian.BaldursGate3", number: 11));
        diagnostic.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>())
            .Should().Be("The module Alpha is installed twice, as alpha.pak and as alpha-ab12.pak.");
    }

    [Fact]
    public async Task WithoutAFileTheCurrentGameDefaultsAreWritten()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        OnDisk(ModsettingsPath).Delete();

        await Apply(loadout);

        var disk = Disk();
        disk.VersionAttributes.Should().Equal(new Dictionary<string, string>
        {
            ["major"] = "4", ["minor"] = "8", ["revision"] = "0", ["build"] = "400",
        });
        disk.Entries.Select(e => (e.Name, e.Uuid, e.Version64)).Should().Equal(
            ("GustavX", GustavUuid, "145241302737902957"),
            ("HonourX", HonourUuid, "36028797025827000"),
            ("Alpha", AlphaUuid, SyntheticPak.Version(1).ToString()));
    }

    [Fact]
    public async Task AnOrderNeverSeededTakesTheGameFilesOrderOnTheFirstApply()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await InstallMod(loadout, "Beta mod", "beta.pak", BetaPak());
        await WriteModsettings([.. BaseEntries, Entry("Beta", "Beta", "0", BetaUuid), Entry("Alpha", "Alpha", "0", AlphaUuid)]);
        using (var tx = Connection.BeginTransaction())
        {
            // An order made before the file was ever read: created, never seeded.
            tx.Add(SortOrderOf(loadout).Value, Bg3PakSortOrder.SeededFromGameFile, false);
            await tx.Commit();
        }

        await Apply(loadout);

        DiskNames().Should().Equal("GustavX", "HonourX", "Beta", "Alpha");
    }

    [Fact]
    public async Task APakSetUpByHandKeepsItsPlaceInTheGameFileOnceTheLoadoutHoldsIt()
    {
        await WritePak("plain-one.pak", SyntheticPak.ForModule("PlainOne", "Plain One", ThirdPartyUuid, SyntheticPak.Version(1)));
        await WritePak("plain-two.pak", SyntheticPak.ForModule("PlainTwo", "Plain Two", PlainTwoUuid, SyntheticPak.Version(1)));
        await WriteModsettings([.. BaseEntries, Entry("PlainTwo", "Plain Two", "0", PlainTwoUuid), Entry("PlainOne", "Plain One", "0", ThirdPartyUuid)]);
        var loadout = await CreateLoadout();

        // The first Apply takes the paks into the loadout, the second writes them as loadout paks.
        await Apply(loadout);
        await Apply(loadout);

        DiskNames().Should().Equal("GustavX", "HonourX", "Plain Two", "Plain One");
    }

    [Fact]
    public async Task TheStoredListsFollowEachApply()
    {
        var loadout = await CreateLoadout();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        var beta = await InstallMod(loadout, "Beta mod", "beta.pak", BetaPak());
        await WriteModsettings(BaseEntries);

        await Apply(loadout);
        Lines(Notice(loadout)!.Value.LastWrittenPaks).Should().Equal("alpha.pak", "beta.pak");

        await DisableItem(beta.Value);
        await Apply(loadout);
        var notice = Notice(loadout)!.Value;
        Lines(notice.LastWrittenPaks).Should().Equal("alpha.pak");
        Lines(notice.PreviousWrittenPaks).Should().Equal("alpha.pak", "beta.pak");
    }
}
