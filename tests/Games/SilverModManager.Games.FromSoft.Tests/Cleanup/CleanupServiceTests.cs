using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;
using NSubstitute;

namespace SilverModManager.Games.FromSoft.Tests.Cleanup;

/// <summary>
/// The cleanup of the dirty fixture <c>Game</c> folder: a swapped launcher with its backup, a mod folder with an
/// ini, a link to a graphics hook, crash dumps and a log, beside the vanilla files.
/// </summary>
public class CleanupServiceTests(ITestOutputHelper helper) : AEldenRingTest<CleanupServiceTests>(helper, dirtyGameFolder: true)
{
    private FakeImmutableProbe Probe { get; } = new();
    private ISteamLaunchOptionWriter LaunchOptions { get; } = Substitute.For<ISteamLaunchOptionWriter>();
    private IGameLaunchSession LaunchSession { get; } = Substitute.For<IGameLaunchSession>();

    protected sealed class FakeImmutableProbe : IImmutableFlagProbe
    {
        public bool Immutable { get; set; }
        public bool IsImmutable(AbsolutePath file) => Immutable;
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services.Replace(ServiceDescriptor.Singleton<IImmutableFlagProbe>(Probe));
        services.Replace(ServiceDescriptor.Singleton(LaunchOptions));
        services.Replace(ServiceDescriptor.Singleton(LaunchSession));
        return services;
    }

    private CleanupService Service => ServiceProvider.GetRequiredService<CleanupService>();

    private AbsolutePath Game => RealGameFolder;

    /// <summary>Where the fixture's graphics hook link points; read before a cleanup removes the link.</summary>
    private string HookTarget => new FileInfo((Game / "dxgi.dll").ToString()).LinkTarget!;

    private void AddMoreLeftovers()
    {
        File.WriteAllText((Game / "ReShade.log").ToString(), "log");
        File.WriteAllText((Game / "vkd3d-proton.cache").ToString(), "cache");
    }

    [Fact]
    public async Task ThePlanListsTheStepsOfAnInstallLeftByAnotherModManagerInOrder()
    {
        AddMoreLeftovers();
        var loadout = await CreateLoadout();

        var plan = Service.Plan(loadout);

        plan.Steps.Select(s => (s.Kind, s.Source)).Should().Equal(
            (CleanupStepKind.Move, "start_protected_game.exe.erm-backup"),
            (CleanupStepKind.Copy, "mods/AdjustTheFov/config.ini"),
            (CleanupStepKind.Delete, "mods/AdjustTheFov"),
            (CleanupStepKind.Delete, "mods"),
            (CleanupStepKind.Delete, "dxgi.dll"),
            (CleanupStepKind.Delete, "ReShade.log"),
            (CleanupStepKind.Delete, "SeamlessCoop"));
    }

    [Fact]
    public async Task PlanningChangesNothing()
    {
        AddMoreLeftovers();
        var loadout = await CreateLoadout();
        var before = SnapshotOfGameFolder();

        Service.Plan(loadout);

        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task WhileTheLauncherIsImmutableTheFirstStepIsACommandAndNothingIsApplied()
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        var before = SnapshotOfGameFolder();

        var plan = Service.Plan(loadout);
        var result = await Service.ApplyAsync(loadout, plan);

        plan.Steps[0].Kind.Should().Be(CleanupStepKind.Privileged);
        plan.Steps[0].Description.Should().Contain("sudo chattr -i").And.Contain((Game / "start_protected_game.exe").ToString());
        plan.Steps[0].RequiredCommand.Argv.Should().Equal("chattr", "-i", (Game / "start_protected_game.exe").ToString());
        plan.Steps[1].IsBlocked.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Contain("immutable");
        result.Done.Should().BeEmpty();
        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task ApplyingRestoresTheLauncherAndRemovesWhatTheOldManagerLeftWithoutTouchingTheRest()
    {
        AddMoreLeftovers();
        var loadout = await CreateLoadout();
        var hook = HookTarget;
        var vanilla = EldenRingInstall.GameFiles.Where(f => f.Path != "Game/start_protected_game.exe")
            .ToDictionary(f => f.Path, f => File.ReadAllBytes((Game.Parent / f.Path).ToString()));
        var plan = Service.Plan(loadout);

        var result = await Service.ApplyAsync(loadout, plan);

        result.Failure.Should().BeNull();
        result.Done.Should().Equal(plan.Steps.Where(s => s.Acts));
        (await (Game / "start_protected_game.exe").ReadAllTextAsync()).Should().Be("anti-cheat launcher");
        (Game / "start_protected_game.exe.erm-backup").FileExists.Should().BeFalse();
        (Game / "mods").DirectoryExists().Should().BeFalse();
        (Game / "SeamlessCoop").DirectoryExists().Should().BeFalse();
        (Game / "ReShade.log").FileExists.Should().BeFalse();
        new FileInfo((Game / "dxgi.dll").ToString()).Exists.Should().BeFalse();
        File.Exists(hook).Should().BeTrue("a link is removed, its target is not followed");
        (await (Game / "vkd3d-proton.cache").ReadAllTextAsync()).Should().Be("cache");
        foreach (var (path, content) in vanilla) File.ReadAllBytes((Game.Parent / path).ToString()).Should().Equal(content);

        Service.Plan(loadout).IsEmpty.Should().BeTrue("the folder is pristine now");
    }

    [Fact]
    public async Task TheIniOfAModLandsInTheLoadoutAndIsDeployedToTheOverlayAtTheSameRelativePath()
    {
        var loadout = await CreateLoadout();
        var content = await (Game / "mods/AdjustTheFov/config.ini").ReadAllTextAsync();

        await Service.ApplyAsync(loadout, Service.Plan(loadout));

        var path = new GamePath(LocationId.Me3Mods, "overlay/mods/AdjustTheFov/config.ini");
        var refreshed = Refresh(loadout);
        var item = LoadoutItem.FindByLoadout(refreshed.Db, refreshed).OfTypeLoadoutItemWithTargetPath().Single(i => i.TargetPath == path);
        item.TryGetAsLoadoutFile(out var file).Should().BeTrue();
        file.Hash.Should().Be(Encoding.UTF8.GetBytes(content).xxHash3());
        (await FileStore.HaveFile(file.Hash)).Should().BeTrue();

        await Synchronizer.Synchronize(Refresh(loadout));

        (await (GameInstallation.Locations[LocationId.Me3Mods].Path / "overlay/mods/AdjustTheFov/config.ini").ReadAllTextAsync()).Should().Be(content);
        Game.EnumerateFiles().Select(f => f.FileName).Should().NotContain("config.ini");
    }

    [Fact]
    public async Task EveryLinkIsRecordedWithItsTargetBeforeItIsRemoved()
    {
        var loadout = await CreateLoadout();
        var hook = HookTarget;

        await Service.ApplyAsync(loadout, Service.Plan(loadout));

        var settings = ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>();
        var folder = EldenRingPaths.CleanupFolder(settings, ServiceProvider.GetRequiredService<IFileSystem>(), EldenRingPaths.InstallationFolderName(GameInstallation.LocatorResult));
        var links = JsonDocument.Parse(await (folder / "links.json").ReadAllTextAsync()).RootElement.EnumerateArray().ToArray();
        links.Should().ContainSingle().Which.GetProperty("name").GetString().Should().Be("dxgi.dll");
        links[0].GetProperty("target").GetString().Should().Be(hook);
    }

    [Fact]
    public async Task ARecordOfEarlierLinksIsKeptWhenALaterRunHasNone()
    {
        var loadout = await CreateLoadout();
        await Service.ApplyAsync(loadout, Service.Plan(loadout));

        var again = await Service.ApplyAsync(loadout, Service.Plan(loadout));

        again.Succeeded.Should().BeTrue();
        var settings = ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>();
        var folder = EldenRingPaths.CleanupFolder(settings, ServiceProvider.GetRequiredService<IFileSystem>(), EldenRingPaths.InstallationFolderName(GameInstallation.LocatorResult));
        JsonDocument.Parse(await (folder / "links.json").ReadAllTextAsync()).RootElement.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ApplyRefusesWhileTheInstallationLockIsHeld()
    {
        var loadout = await CreateLoadout();
        LaunchSession.IsRunning(Arg.Any<GameInstallation>()).Returns(true);
        var before = SnapshotOfGameFolder();

        var result = await Service.ApplyAsync(loadout, Service.Plan(loadout));

        result.Failure.Should().Contain("running");
        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task ApplyRefusesAPlanTheFolderNoLongerMatches()
    {
        var loadout = await CreateLoadout();
        var plan = Service.Plan(loadout);
        File.WriteAllText((Game / "ReShade.log").ToString(), "appeared after the plan was shown");

        var result = await Service.ApplyAsync(loadout, plan);

        result.Failure.Should().Contain("changed");
        (Game / "mods").DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public async Task AFileTheGameDoesNotShipIsReviewedAndLeftInPlace()
    {
        File.WriteAllText((Game / "mystery.bin").ToString(), "?");
        var loadout = await CreateLoadout();
        var plan = Service.Plan(loadout);

        var result = await Service.ApplyAsync(loadout, plan);

        plan.Steps.Should().ContainSingle(s => s.Kind == CleanupStepKind.Review && s.Source == "mystery.bin");
        result.Succeeded.Should().BeTrue();
        (Game / "mystery.bin").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task ErmsMe3LaunchOptionsAreOfferedForReplacementAndReplacedAfterTheFiles()
    {
        var loadout = await CreateLoadout();
        const string oldLine = "/home/u/.local/bin/me3 launch --game er -- %command%";
        WriteLocalConfig(oldLine);
        LaunchOptions.ComposeLine(Arg.Any<Loadout.ReadOnly>()).Returns("'/shim' 'Main' %command%");
        LaunchOptions.Replace(Arg.Any<GameInstallation>(), Arg.Any<Loadout.ReadOnly>(), oldLine)
            .Returns(new LocalConfigVdfWriter.Result("'/shim' 'Main' %command%", null, null));

        var plan = Service.Plan(loadout);
        var result = await Service.ApplyAsync(loadout, plan);

        var step = plan.Steps[^1];
        step.Kind.Should().Be(CleanupStepKind.ReplaceLaunchOption);
        (step.Source, step.Target).Should().Be((oldLine, "'/shim' 'Main' %command%"));
        result.Succeeded.Should().BeTrue();
        LaunchOptions.Received(1).Replace(Arg.Any<GameInstallation>(), Arg.Any<Loadout.ReadOnly>(), oldLine);
    }

    [Fact]
    public async Task AReplacementSteamRefusesIsReportedAfterTheFileStepsHaveRun()
    {
        var loadout = await CreateLoadout();
        const string oldLine = "me3 launch -- %command%";
        WriteLocalConfig(oldLine);
        LaunchOptions.ComposeLine(Arg.Any<Loadout.ReadOnly>()).Returns("SPAN");
        LaunchOptions.Replace(Arg.Any<GameInstallation>(), Arg.Any<Loadout.ReadOnly>(), oldLine)
            .Returns(new LocalConfigVdfWriter.Result(null, "Steam is running; close it first.", null));

        var result = await Service.ApplyAsync(loadout, Service.Plan(loadout));

        result.Failure.Should().Be("Steam is running; close it first.");
        (Game / "mods").DirectoryExists().Should().BeFalse("the file steps ran first and a second run only has the launch options left");
        Service.Plan(loadout).Steps.Should().ContainSingle().Which.Kind.Should().Be(CleanupStepKind.ReplaceLaunchOption);
    }

    [Fact]
    public async Task LaunchOptionsThatStartNoOtherManagerAreLeftAlone()
    {
        var loadout = await CreateLoadout();
        WriteLocalConfig("PROTON_LOG=1 %command%");

        Service.Plan(loadout).Steps.Should().NotContain(s => s.Kind == CleanupStepKind.ReplaceLaunchOption);
    }

    [Fact]
    public async Task TheGuideDescribesTheStepsAndTheCommandThatRunsThem()
    {
        var loadout = await CreateLoadout();
        var guide = ServiceProvider.GetServices<IGameCleanupGuide>().Single();

        var lines = guide.Describe(loadout);

        guide.AppliesTo(GameInstallation).Should().BeTrue();
        lines.Should().Contain(l => l.Contains("Move start_protected_game.exe.erm-backup over start_protected_game.exe"));
        lines.Should().Contain(l => l.StartsWith("silvermod elden-ring cleanup --apply --loadout '"));
    }

    private void WriteLocalConfig(string launchOptions)
    {
        var path = GameInstallation.LocatorResult.LinuxCompatabilityDataProvider!.SteamLocalConfigPath;
        path.Parent.CreateDirectory();
        var line = launchOptions.Replace("\\", "\\\\").Replace("\"", "\\\"");
        File.WriteAllText(path.ToString(),
            "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n"
            + $"\t\t\t\t\"apps\"\n\t\t\t\t{{\n\t\t\t\t\t\"{EldenRingPaths.SteamAppId}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"{line}\"\n\t\t\t\t\t}}\n\t\t\t\t}}\n"
            + "\t\t\t}\n\t\t}\n\t}\n}\n");
    }
}

/// <summary>Steam has an update for the game it has not applied: the cleanup is still allowed, and says to let Steam finish.</summary>
public class CleanupWithAPendingSteamUpdateTests(ITestOutputHelper helper) : AEldenRingTest<CleanupWithAPendingSteamUpdateTests>(helper, dirtyGameFolder: true)
{
    protected override SilverModManager.Games.TestFramework.Helpers.SteamInstall InstallGame(SilverModManager.Games.TestFramework.Helpers.SteamLibraryFixture library)
    {
        var install = base.InstallGame(library);
        var manifest = install.AppManifest.ToString();
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"StateFlags\"\t\t\"4\"", "\"StateFlags\"\t\t\"6\""));
        return install;
    }

    private IImmutableFlagProbe Probe { get; } = Substitute.For<IImmutableFlagProbe>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services.Replace(ServiceDescriptor.Singleton(Probe));
        services.Replace(ServiceDescriptor.Singleton(Substitute.For<ISteamLaunchOptionWriter>()));
        return services;
    }

    [Fact]
    public async Task ThePlanStillAppliesAndEndsWithAdviceToLetSteamFinish()
    {
        var loadout = await CreateLoadout();
        var service = ServiceProvider.GetRequiredService<CleanupService>();

        var plan = service.Plan(loadout);
        var result = await service.ApplyAsync(loadout, plan);

        plan.Steps[^1].Kind.Should().Be(CleanupStepKind.Advice);
        plan.IsBlocked.Should().BeFalse();
        result.Succeeded.Should().BeTrue();
        (RealGameFolder / "mods").DirectoryExists().Should().BeFalse();
    }
}
