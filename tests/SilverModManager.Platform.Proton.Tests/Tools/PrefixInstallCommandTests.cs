using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Tools;

public class PrefixInstallCommandTests
{
    private static readonly AbsolutePath Path = new InMemoryFileSystem(OSInformation.FakeUnix).FromUnsanitizedFullPath("/wine");

    [Fact]
    public void ASteamPrefixUsesProtontricks()
        => PrefixInstallCommand.For(new ResolvedPrefix(Path, CompatibilityLayerKind.SteamProton, ProtonAppId: 611670), "vb6run")
            .Should().Be("protontricks 611670 -q vb6run");

    [Fact]
    public void AnyOtherPrefixUsesWinetricksWithThePrefixPath()
        => PrefixInstallCommand.For(new ResolvedPrefix(Path, CompatibilityLayerKind.PlainWine), "vb6run")
            .Should().Be("WINEPREFIX=\"/wine\" winetricks -q vb6run");
}
