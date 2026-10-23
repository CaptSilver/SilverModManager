using FluentAssertions;
using SilverModManager.Abstractions.Games;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class LaunchPlannerTests
{
    private const string SkyrimRoot = "/games/steamapps/common/Skyrim Special Edition";

    private static AbsolutePath Path_(string path) => FileSystem.Shared.FromUnsanitizedFullPath(path);

    private static string[] SteamCommand(string exe, params string[] gameArgs) =>
    [
        "/steam/ubuntu12_32/steam-launch-wrapper", "--", "/steam/ubuntu12_32/reaper", "SteamLaunch", "AppId=489830",
        "--", "/steam/entry-point", "--verb=waitforexitandrun", "--", "/steam/proton", "waitforexitandrun", exe, ..gameArgs,
    ];

    private static LaunchPlan Compose(string[] command, string specExe, params string[] extraArgs)
        => LaunchPlanner.Compose(command, Path_(SkyrimRoot), Path_($"{SkyrimRoot}/SkyrimSE.exe"), new LaunchSpec(Path_(specExe), extraArgs), [], []);

    [Fact]
    public void FilesystemRootsComeFromTheDeploymentPlan()
    {
        var command = SteamCommand($"{SkyrimRoot}/SkyrimSE.exe");

        var plan = LaunchPlanner.Compose(command, Path_(SkyrimRoot), Path_($"{SkyrimRoot}/SkyrimSE.exe"),
            new LaunchSpec(Path_($"{SkyrimRoot}/SkyrimSE.exe"), []), ["/run/media/store"], []);

        plan.Process.FilesystemRoots.Should().Equal("/run/media/store");
    }

    [Fact]
    public void ALoaderTakesTheLaunchersPlaceAndTheCommandIsStartedAsRewritten()
    {
        var plan = Compose(SteamCommand($"{SkyrimRoot}/SkyrimSELauncher.exe", "-windowed"), $"{SkyrimRoot}/skse64_loader.exe");

        plan.Process.Argv.Should().Equal(SteamCommand($"{SkyrimRoot}/skse64_loader.exe", "-windowed"));
        plan.Resolved!.Exe.FileName.Should().Be("skse64_loader.exe");
        plan.Substitution!.IsRefused.Should().BeFalse();
    }

    [Fact]
    public void AToolThatResolvedTheGamesOwnExeLeavesAMatchingCommandAlone()
    {
        var command = SteamCommand($"{SkyrimRoot}/SkyrimSE.exe");

        Compose(command, $"{SkyrimRoot}/SkyrimSE.exe").Process.Argv.Should().Equal(command);
    }

    [Fact]
    public void ALoaderWithNoExeToReplaceIsRefusedWithTheCommandInTheText()
    {
        var command = new[] { "/usr/bin/true", "--flag" };

        var act = () => Compose(command, $"{SkyrimRoot}/skse64_loader.exe");

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("/usr/bin/true --flag");
    }

    [Fact]
    public void ArgumentsTheGameNeedsCountAsAResolvedLoaderEvenForTheGamesOwnExe()
    {
        var act = () => Compose(["/usr/bin/true"], $"{SkyrimRoot}/SkyrimSE.exe", "-modded");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WithNothingResolvedAndNoExeToReplaceTheCommandRunsUnchanged()
    {
        var command = new[] { "/usr/bin/true", "--flag" };

        Compose(command, $"{SkyrimRoot}/SkyrimSE.exe").Process.Argv.Should().Equal(command);
    }

    [Fact]
    public void TheGamesWineDllOverridesLandOnTheSpec()
    {
        WineDllOverride[] overrides = [new("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])];

        var plan = LaunchPlanner.Compose(SteamCommand($"{SkyrimRoot}/SkyrimSE.exe"), Path_(SkyrimRoot), Path_($"{SkyrimRoot}/SkyrimSE.exe"),
            new LaunchSpec(Path_($"{SkyrimRoot}/SkyrimSE.exe"), []), [], overrides);

        plan.Process.WineDllOverrides.Should().Equal(overrides);
    }

    private const string EldenRingRoot = "/games/steamapps/common/ELDEN RING";

    private static string[] SteamChainFor(string exe) =>
    [
        "/steam/ubuntu12_32/steam-launch-wrapper", "--", "/steam/ubuntu12_32/reaper", "SteamLaunch", "AppId=1245620",
        "--", "/steam/pressure-vessel/bin/pressure-vessel-launch", "--", "/steam/proton", "waitforexitandrun", exe,
    ];

    private static LaunchPlan ComposeReplacing(IReadOnlyList<string> command, string[] extraArgs, IReadOnlyList<string> roots, IReadOnlyList<WineDllOverride> overrides)
    {
        var spec = new LaunchSpec(Path_("/usr/bin/bwrap"), extraArgs) { ReplacesCommand = true };
        return LaunchPlanner.Compose(command, Path_(EldenRingRoot), Path_($"{EldenRingRoot}/Game/eldenring.exe"), spec, roots, overrides);
    }

    [Fact]
    public void ASpecWithoutTheFlagKeepsTheCommandSteamHandedOver()
    {
        new LaunchSpec(Path_($"{SkyrimRoot}/SkyrimSE.exe"), []).ReplacesCommand.Should().BeFalse();
    }

    [Fact]
    public void ASpecThatReplacesTheCommandDiscardsSteamsWholeChain()
    {
        var plan = ComposeReplacing(SteamChainFor($"{EldenRingRoot}/Game/eldenring.exe"), ["--unshare-user", "--", "/me3", "launch"], [], []);

        plan.Process.Argv.Should().Equal("/usr/bin/bwrap", "--unshare-user", "--", "/me3", "launch");
        plan.Process.Argv.Should().NotContain(a => a.Contains("reaper") || a.Contains("pressure-vessel") || a.Contains("proton"));
    }

    [Fact]
    public void ASpecThatReplacesTheCommandStillReadsTheAppIdFromSteamsChain()
    {
        var plan = ComposeReplacing(SteamChainFor($"{EldenRingRoot}/Game/eldenring.exe"), [], [], []);

        plan.Substitution!.AppId.Should().Be("1245620");
        plan.Substitution.IsRefused.Should().BeFalse();
        plan.Substitution.Argv.Should().Equal(plan.Process.Argv);
    }

    [Fact]
    public void ASpecThatReplacesTheCommandCarriesTheRootsAndTheOverridesThrough()
    {
        WineDllOverride[] overrides = [new("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])];

        var plan = ComposeReplacing(SteamChainFor($"{EldenRingRoot}/Game/eldenring.exe"), [], ["/data/mods", "/data/upper"], overrides);

        plan.Process.FilesystemRoots.Should().Equal("/data/mods", "/data/upper");
        plan.Process.WineDllOverrides.Should().Equal(overrides);
    }

    [Fact]
    public void ASpecThatReplacesTheCommandComposesFromAnEmptySteamCommand()
    {
        var plan = ComposeReplacing([], ["--", "/me3"], [], []);

        plan.Process.Argv.Should().Equal("/usr/bin/bwrap", "--", "/me3");
        plan.Substitution!.AppId.Should().BeNull();
    }
}
