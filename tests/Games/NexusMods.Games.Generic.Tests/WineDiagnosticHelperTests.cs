using System.Collections.Immutable;
using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.Generic.Tests;

public class WineDiagnosticHelperTests
{
    [Fact]
    public async Task SteamInstructionsPointAtTheDialogAndTheVerbAndCarryTheMergedValue()
    {
        var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
        provider.GetWineDllOverrides(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableArray<WineDllOverride>>(ImmutableArray<WineDllOverride>.Empty));

        var result = new GameLocatorResult
        {
            Game = Substitute.For<IGameData>(),
            Path = FileSystem.Shared.FromUnsanitizedFullPath("/games/test"),
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
            LinuxCompatabilityDataProvider = provider,
        };
        var installation = new GameInstallation(result, default);

        WineDllOverride[] required =
        [
            new("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
            new("version", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
        ];

        var instructions = await WineDiagnosticHelper.GetWineDllOverridesUpdateInstructions(installation, required, []);

        instructions.Should().Contain("Use this loadout from Steam");
        instructions.Should().Contain("steam-launch-option");
        instructions.Should().Contain("WINEDLLOVERRIDES=\"winmm=n,b;version=n,b\"");
    }

    [Fact]
    public async Task NothingIsReportedWhenNothingIsRequired()
    {
        var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
        var result = new GameLocatorResult
        {
            Game = Substitute.For<IGameData>(),
            Path = FileSystem.Shared.FromUnsanitizedFullPath("/games/test"),
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
            LinuxCompatabilityDataProvider = provider,
        };

        var instructions = await WineDiagnosticHelper.GetWineDllOverridesUpdateInstructions(new GameInstallation(result, default), [], []);

        instructions.Should().BeNull();
    }

    [Fact]
    public async Task SteamInstructionsEndTheHandWrittenLineWithTheLauncherSkipArguments()
    {
        var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
        provider.GetWineDllOverrides(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableArray<WineDllOverride>>(ImmutableArray<WineDllOverride>.Empty));
        var installation = SteamInstallation(provider);
        WineDllOverride[] required = [new("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])];

        var instructions = await WineDiagnosticHelper.GetWineDllOverridesUpdateInstructions(installation, required, ["--launcher-skip"]);

        instructions.Should().Contain("WINEDLLOVERRIDES=\"winmm=n,b\" %command% --launcher-skip");
    }

    [Fact]
    public async Task SteamInstructionsForAGameWithoutALauncherEndAtCommand()
    {
        var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
        provider.GetWineDllOverrides(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ImmutableArray<WineDllOverride>>(ImmutableArray<WineDllOverride>.Empty));
        WineDllOverride[] required = [new("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])];

        var instructions = await WineDiagnosticHelper.GetWineDllOverridesUpdateInstructions(SteamInstallation(provider), required, []);

        instructions.Should().Contain("WINEDLLOVERRIDES=\"winmm=n,b\" %command%\n");
        instructions.Should().NotContain("--launcher-skip");
    }

    private static GameInstallation SteamInstallation(ILinuxCompatabilityDataProvider provider)
    {
        var result = new GameLocatorResult
        {
            Game = Substitute.For<IGameData>(),
            Path = FileSystem.Shared.FromUnsanitizedFullPath("/games/test"),
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
            LinuxCompatabilityDataProvider = provider,
        };
        return new GameInstallation(result, default);
    }
}
