using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.FromSoft.Tests.Overlay;

public class OverlayLaunchGateTests(ITestOutputHelper helper) : AModdedEldenRingTest<OverlayLaunchGateTests>(helper)
{
    private string RealGame(string relative) => Path.Combine(RealGameFolder.GetFullPath(), relative);

    [Fact]
    public async Task AnUnmoddedLoadoutIsNeverVetoedNorPreparedEvenWithoutBwrapOrMe3()
    {
        var loadout = await CreateLoadout();
        Executables.Bwrap = null;
        var context = ContextOf(loadout);

        Gate.Check(context).Should().BeNull();
        await Gate.Prepare(context, CancellationToken.None);
        await Gate.Finish(context);

        Directory.Exists(LayoutOf(loadout).Upper.GetFullPath()).Should().BeFalse();
        Probe.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AModdedLoadoutThatIsReadyPassesTheCheck()
    {
        var loadout = await CreateModdedLoadout();

        Gate.Check(ContextOf(loadout)).Should().BeNull();
        Probe.Calls.Should().Be(1);
    }

    [Fact]
    public async Task WithoutBwrapTheCheckVetoesAndSaysSo()
    {
        var loadout = await CreateModdedLoadout();
        Executables.Bwrap = null;

        Gate.Check(ContextOf(loadout))!.Message.Should().Contain("bwrap was not found");
    }

    [Fact]
    public async Task AProbeThatFailsVetoesWithItsReason()
    {
        var loadout = await CreateModdedLoadout();
        Probe.Failure = "it exited with code 1: Operation not permitted";

        Gate.Check(ContextOf(loadout))!.Message.Should().Contain("user namespace").And.Contain("Operation not permitted");
    }

    [Fact]
    public async Task APendingSteamUpdateVetoesAndNamesIt()
    {
        var loadout = await CreateModdedLoadout();
        var pending = ContextOf(loadout) with { Installation = WithUpdatePending(GameInstallation) };

        Gate.Check(pending)!.Message.Should().Contain("Steam update is waiting").And.Contain("finish");
    }

    [Fact]
    public async Task AMissingMe3BinaryOnDiskVetoes()
    {
        var loadout = await CreateModdedLoadout();
        File.Delete((GameInstallation.Locations[LocationId.Me3Host].Path / Me3HostFiles.Binary).GetFullPath());

        Gate.Check(ContextOf(loadout))!.Message.Should().Contain("me3 is not deployed");
    }

    [Fact]
    public async Task AnUnresolvedFindingOfThePreviousSessionVetoesUntilItIsDiscarded()
    {
        var loadout = await CreateModdedLoadout();
        var layout = LayoutOf(loadout);
        await Gate.Prepare(ContextOf(loadout), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(layout.Upper.GetFullPath(), "stray.txt"), "x");
        await Gate.Finish(ContextOf(loadout));

        Gate.Check(ContextOf(loadout))!.Message.Should().Contain("stray.txt").And.Contain("unresolved").And.Contain(layout.Findings.ToString());

        UpperdirAudit.Discard(layout, UpperdirAudit.PendingFindings(layout).Single());
        Gate.Check(ContextOf(loadout)).Should().BeNull();
    }

    [Fact]
    public async Task PrepareWritesTheConfigLayerIntoTheUpperFolderWithThePasswordInOnlyThatCopy()
    {
        var loadout = await CreateModdedLoadout(seamlessCoop: true);
        var layout = LayoutOf(loadout);

        await Gate.Prepare(ContextOf(loadout), CancellationToken.None);

        var upperSettings = Path.Combine(layout.Upper.GetFullPath(), "SeamlessCoop/ersc_settings.ini");
        (await File.ReadAllTextAsync(upperSettings)).Should().Contain($"cooppassword = {Password}");
        File.GetUnixFileMode(upperSettings).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Exists(Path.Combine(layout.Upper.GetFullPath(), "SeamlessCoop/ersc.dll")).Should().BeTrue();

        // The loadout's own file, as deployed from the file store, still holds the template.
        var deployed = GameInstallation.Locations[LocationId.Me3Mods].Path / "overlay/SeamlessCoop/ersc_settings.ini";
        (await deployed.ReadAllTextAsync()).Should().Be(ErscRelease.SettingsText);

        // And the password is nowhere else under the app's data.
        var dataRoot = EldenRingPaths.Me3Root(ServiceProvider.GetRequiredService<NexusMods.Sdk.Settings.ISettingsManager>().Get<FromSoftSettings>(), FileSystem).GetFullPath();
        var holders = Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(layout.Upper.GetFullPath()) && File.ReadAllText(f).Contains(Password));
        holders.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareMakesTheMe3BinaryExecutable()
    {
        var loadout = await CreateModdedLoadout();
        var me3 = (GameInstallation.Locations[LocationId.Me3Host].Path / Me3HostFiles.Binary).GetFullPath();
        File.SetUnixFileMode(me3, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await Gate.Prepare(ContextOf(loadout), CancellationToken.None);

        File.GetUnixFileMode(me3).Should().HaveFlag(UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task APrepareWrittenCopyInTheConfigLayerRaisesNothingEvenWhenTheGameFolderHasTheSameFile()
    {
        var loadout = await CreateModdedLoadout(seamlessCoop: true);
        Directory.CreateDirectory(RealGame("SeamlessCoop"));
        await File.WriteAllTextAsync(RealGame("SeamlessCoop/ersc_settings.ini"), ErscRelease.SettingsText);
        await File.WriteAllTextAsync(RealGame("SeamlessCoop/ersc.dll"), "dll");

        await Gate.Prepare(ContextOf(loadout), CancellationToken.None);
        await Gate.Finish(ContextOf(loadout));

        UpperdirAudit.PendingFindings(LayoutOf(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task AFileOfAnEarlierSessionThatIsNoLongerInTheConfigLayerIsRemovedFromTheUpperFolder()
    {
        var loadout = await CreateModdedLoadout(seamlessCoop: true);
        var layout = LayoutOf(loadout);
        await Gate.Prepare(ContextOf(loadout), CancellationToken.None);
        File.Delete((GameInstallation.Locations[LocationId.Me3Mods].Path / "overlay/SeamlessCoop/ersc.dll").GetFullPath());

        await Gate.Prepare(ContextOf(loadout), CancellationToken.None);

        File.Exists(Path.Combine(layout.Upper.GetFullPath(), "SeamlessCoop/ersc.dll")).Should().BeFalse();
        File.Exists(Path.Combine(layout.Upper.GetFullPath(), "SeamlessCoop/ersc_settings.ini")).Should().BeTrue();
    }

    [Fact]
    public async Task FinishAuditsTheUpperFolder()
    {
        var loadout = await CreateModdedLoadout();
        var layout = LayoutOf(loadout);
        await File.WriteAllTextAsync(RealGame("DLC.bdt"), "archive");
        await Gate.Prepare(ContextOf(loadout), CancellationToken.None);

        var upper = layout.Upper.GetFullPath();
        Directory.CreateDirectory(Path.Combine(upper, "mods/x"));
        Directory.CreateDirectory(Path.Combine(upper, "SeamlessCoop/crashdumps"));
        await File.WriteAllTextAsync(Path.Combine(upper, "mods/x/config.ini"), "x");
        await File.WriteAllTextAsync(Path.Combine(upper, "SeamlessCoop/crashdumps/a.dmp"), "x");
        await File.WriteAllTextAsync(Path.Combine(upper, "DLC.bdt"), "x");
        await File.WriteAllTextAsync(Path.Combine(upper, "stray.txt"), "x");
        await Gate.Finish(ContextOf(loadout));

        UpperdirAudit.PendingFindings(layout).Should().Equal(
            new AuditFinding(AuditFindingKind.ShadowedGameFile, "DLC.bdt"),
            new AuditFinding(AuditFindingKind.Unexpected, "stray.txt"));
    }

    [Fact]
    public async Task AFileWrittenIntoTheRealGameFolderDuringTheSessionIsCriticalAndNamed()
    {
        var loadout = await CreateModdedLoadout();
        await Gate.Prepare(ContextOf(loadout), CancellationToken.None);

        await File.WriteAllTextAsync(RealGame("leaked.dll"), "x");
        await File.WriteAllTextAsync(RealGame("regulation.bin"), "tampered");
        File.Delete(RealGame("steam_api64.dll"));
        await Gate.Finish(ContextOf(loadout));

        UpperdirAudit.PendingFindings(LayoutOf(loadout)).Should().Equal(
            new AuditFinding(AuditFindingKind.GameFolderChanged, "leaked.dll", "added"),
            new AuditFinding(AuditFindingKind.GameFolderChanged, "regulation.bin", "changed"),
            new AuditFinding(AuditFindingKind.GameFolderChanged, "steam_api64.dll", "removed"));
        Gate.Check(ContextOf(loadout))!.Message.Should().Contain("leaked.dll");
    }

    private static GameInstallation WithUpdatePending(GameInstallation installation)
    {
        var result = installation.LocatorResult;
        return new GameInstallation(new GameLocatorResult
        {
            Game = result.Game,
            Path = result.Path,
            Store = result.Store,
            StoreIdentifier = result.StoreIdentifier,
            LocatorIds = result.LocatorIds,
            Locator = result.Locator,
            Platform = result.Platform,
            StoreUpdatePending = true,
        }, installation.Locations);
    }
}
