using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Abstractions.Games.Tests;

public class LaunchPlatformTests
{
    private static readonly AbsolutePath SomeFolder = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory);

    private static GameLocatorResult Located(GameStore store, OSPlatform platform, CompatibilityLayer? compatibility = null) => new()
    {
        Game = Substitute.For<IGameData>(),
        Path = SomeFolder,
        Store = store,
        StoreIdentifier = "1",
        LocatorIds = ImmutableArray<LocatorId>.Empty,
        Locator = Substitute.For<IGameLocator>(),
        Platform = platform,
        Compatibility = compatibility,
    };

    private static readonly PrefixResolution NoPrefix = PrefixResolution.Failed("no prefix");

    [Fact]
    public void ANativeBuild_IsNotAWindowsProgram()
        => LaunchPlatform.IsWindowsProgram(PrefixResolution.Native("native"), Located(GameStore.Steam, OSPlatform.Windows)).Should().BeFalse();

    [Fact]
    public void ABuildWithAPrefix_IsAWindowsProgram()
        => LaunchPlatform.IsWindowsProgram(PrefixResolution.Resolved(new ResolvedPrefix(SomeFolder, CompatibilityLayerKind.SteamProton)), Located(GameStore.Steam, OSPlatform.Linux))
            .Should().BeTrue();

    [Fact]
    public void AWindowsBuildWithoutAPrefix_IsAWindowsProgram()
        => LaunchPlatform.IsWindowsProgram(NoPrefix, Located(GameStore.Steam, OSPlatform.Windows)).Should().BeTrue();

    [Fact]
    public void ABuildWithACompatibilityLayerButNoReadyPrefix_IsAWindowsProgram()
    {
        // Heroic records the layer but not the platform.
        var heroic = Located(GameStore.GOG, OSPlatform.Linux, new CompatibilityLayer(CompatibilityLayerKind.Heroic, SomeFolder));

        LaunchPlatform.IsWindowsProgram(NoPrefix, heroic).Should().BeTrue();
    }

    [Fact]
    public void AManuallyAddedBuildWithNothingSayingWindows_IsNotAWindowsProgram()
        => LaunchPlatform.IsWindowsProgram(NoPrefix, Located(GameStore.ManuallyAdded, OSPlatform.Linux)).Should().BeFalse();
}
