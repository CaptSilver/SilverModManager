using FluentAssertions;
using NexusMods.Abstractions.Games;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;

namespace NexusMods.Platform.Proton.Tests.Launch;

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
        => LaunchPlanner.Compose(command, Path_(SkyrimRoot), Path_($"{SkyrimRoot}/SkyrimSE.exe"), new LaunchSpec(Path_(specExe), extraArgs));

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
}
