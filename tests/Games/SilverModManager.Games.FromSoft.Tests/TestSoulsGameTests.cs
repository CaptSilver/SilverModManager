using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Sdk.Games;
using DynamicData.Kernel;

namespace SilverModManager.Games.FromSoft.Tests;

/// <summary>
/// Test Souls shares no value with Elden Ring and the base is registered without Elden Ring, so each test here fails
/// if the base reads an Elden Ring value instead of the game's.
/// </summary>
public class TestSoulsGameTests(ITestOutputHelper helper) : AModdedTestSoulsTest<TestSoulsGameTests>(helper)
{
    [Fact]
    public void TheSteamLocatorFindsTheGameByItsAppId()
    {
        GameInstallation.Game.Should().BeOfType<TestSoulsGame>();
        GameInstallation.LocatorResult.StoreIdentifier.Should().Be("424242");
        GameInstallation.Game.GameId.Should().Be(GameId.From("FromSoft.TestSouls"));
        GameInstallation.Game.DisplayName.Should().Be("Test Souls");
        GameInstallation.Game.NexusModsGameId.Should().Be(Optional<Sdk.NexusModsApi.NexusModsGameId>.None);
    }

    [Fact]
    public void TheGameReadsItsFormatTraitsFromItsDefinition()
    {
        var game = (IFromSoftGame)GameInstallation.Game;

        game.Formats.Should().BeSameAs(TestSoulsDefinition.Definition.Formats);
        game.Formats.ParamdefSet.Should().Be("testsouls");
    }

    [Fact]
    public void TheModsAndHostLocationsAreUnderTheGamesOwnFolderAndAppId()
    {
        var root = FromSoftPaths.DataRoot(TestSoulsDefinition.Layout, Settings, FileSystem);

        GameInstallation.Locations[LocationId.Me3Mods].Path.Should().Be(root / "424242/mods");
        GameInstallation.Locations[LocationId.Me3Host].Path.Should().Be(root / "424242/me3");
    }

    [Fact]
    public void ThePrimaryFileIsTheGamesExecutableInItsSubfolderBecauseThereIsNoLauncher()
    {
        Game.GetPrimaryFile(GameInstallation).Should().Be(new GamePath(LocationId.Game, "Bin/testsouls.exe"));
    }

    [Fact]
    public void TheLocalVersionIsReadFromTheGamesExecutable()
    {
        // Any file with a version resource stands in for the game's executable.
        var standIn = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(typeof(TestSoulsGame).Assembly.Location);
        var expected = standIn.FileInfo.GetFileVersionInfo().ProductVersion;
        File.Copy(standIn.ToString(), (Install.Folder / "Bin/testsouls.exe").ToString(), overwrite: true);

        var version = Game.GetLocalVersion(GameInstallation);

        version.HasValue.Should().BeTrue();
        version.Value.Should().Be(expected);
    }

    [Fact]
    public async Task ASyncNeverWritesUnderTheGamesSubfolder()
    {
        var loadout = await CreateModdedLoadout();
        var before = TreeSnapshot.Take(RealGameFolder);

        await Synchronizer.Synchronize(Refresh(loadout));

        TreeSnapshot.Take(RealGameFolder).Should().Equal(before);
    }

    [Fact]
    public async Task TheProfileNamesTheGameAndTheModdedSaveAndHasNoSeamlessCoopLine()
    {
        var loadout = await CreateModdedLoadout();
        await AddModWithFiles(loadout, "Seamless", new GamePath(LocationId.Me3Mods, "overlay/SeamlessCoop/ersc.dll"));
        await Synchronizer.Synchronize(Refresh(loadout));

        var profile = await (GameInstallation.Locations[LocationId.Me3Mods].Path / Me3ModsLayout.ProfileFileName).ReadAllTextAsync();

        profile.Should().Contain("game = \"testsouls\"").And.Contain("savefile = \"TS0000_silver.sl2\"");
        profile.Should().NotContain("SeamlessCoop").And.NotContain("eldenring").And.NotContain("ER0000");
    }

    [Fact]
    public void TheInstallationCheckLooksForNoLauncherAndNoSpawnersTheGameDoesNotName()
    {
        foreach (var name in new[] { "ersc_launcher.exe", "glorious_merchant.dll" })
            File.WriteAllText((RealGameFolder / name).GetFullPath(), "x");
        var check = ServiceProvider.GetServices<IInstallationCheck>().OfType<FromSoftInstallationCheck>().Single();

        var checks = check.Check(GameInstallation);

        checks.Select(c => c.Id).Should().NotContain(["eac", "spawners"]);
        checks.Single(c => c.Id == "artifacts").Level.Should().Be(DoctorCheckLevel.Ok);
    }

    [Fact]
    public void TheCleanupKeepsSeamlessCoopForReviewAndTheGuideDoesNotApply()
    {
        var inventory = new[]
        {
            new InventoryEntry("testsouls.exe", InventoryKind.File, 10),
            new InventoryEntry("SeamlessCoop", InventoryKind.Directory, 0),
            new InventoryEntry("SeamlessCoop/ersc.dll", InventoryKind.File, 10),
        };
        var input = new CleanupInput("/steam/TEST SOULS/Bin", inventory, new HashSet<string> { "testsouls.exe" }, false, null, false);

        var plan = CleanupPlanner.Build(input, TestSoulsDefinition.Definition.Cleanup, TestSoulsDefinition.Layout);

        plan.Steps.Select(s => (s.Kind, s.Source)).Should().Equal((CleanupStepKind.Review, "SeamlessCoop/ersc.dll"));
        // Whether the guide applies is decided from the game alone; the service it would run is never touched.
        new FromSoftCleanupGuide(null!).AppliesTo(GameInstallation).Should().BeFalse();
    }

    [Fact]
    public void TheBuildIsReadFromTheGamesExecutableAndSteamsManifestForItsAppId()
    {
        BuildIdentity.Identify(GameInstallation).Should().Be(new BuildIdentity("1.2.3.4", "16000000"));
    }

    [Fact]
    public void AMissingBuildIdNamesTheManifestOfTheGamesAppId()
    {
        var found = GameInstallation.LocatorResult;
        var withoutBuildId = new GameInstallation(new GameLocatorResult
        {
            Game = found.Game,
            Path = found.Path,
            Store = found.Store,
            StoreIdentifier = found.StoreIdentifier,
            LocatorIds = found.LocatorIds,
            Locator = found.Locator,
            StoreBuildId = "0",
        }, GameInstallation.Locations);

        var act = () => BuildIdentity.Identify(withoutBuildId);

        act.Should().Throw<InvalidOperationException>().WithMessage("*appmanifest_424242.acf*Test Souls*");
    }
}
