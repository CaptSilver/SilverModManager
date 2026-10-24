using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Larian.BaldursGate3;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Larian.Tests;

/// <summary>
/// Baldur's Gate 3 has an official Linux build. It keeps its Mods and PlayerProfiles under XDG_DATA_HOME, not in
/// a Proton prefix, and it starts bin/bg3 rather than the Windows launcher.
/// </summary>
public class NativeBuildTests(IServiceProvider serviceProvider)
{
    private static readonly IFileSystem HostFileSystem = new InMemoryFileSystem(OSInformation.FakeUnix).CreateOverlayFileSystem(
        new Dictionary<AbsolutePath, AbsolutePath>(),
        new Dictionary<KnownPath, AbsolutePath> { [KnownPath.LocalApplicationDataDirectory] = new InMemoryFileSystem(OSInformation.FakeUnix).FromUnsanitizedFullPath("/xdg/data") });

    private global::SilverModManager.Games.Larian.BaldursGate3.BaldursGate3 Game
        => serviceProvider.GetServices<IGame>().OfType<global::SilverModManager.Games.Larian.BaldursGate3.BaldursGate3>().Single();

    [Fact]
    public void BaldursGate3_DeclaresItsNativeBuild()
    {
        Game.HasNativeLinuxBuild.Should().BeTrue();
        Game.NativeLinuxPrimaryFile.Should().Be(RelativePath.FromUnsanitizedInput("bin/bg3"));
    }

    [Fact]
    public void BaldursGate3_Native_ResolvesIntoXdgDataHome()
    {
        var result = NativeResult();
        var larian = HostFileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory).Combine("Larian Studios/Baldur's Gate 3");

        var locations = Game.GetLocations(HostFileSystem, result);

        locations[LocationId.Game].Should().Be(result.Path);
        locations[Bg3Constants.ModsLocationId].Should().Be(larian.Combine("Mods"));
        locations[Bg3Constants.PlayerProfilesLocationId].Should().Be(larian.Combine("PlayerProfiles/Public"));
        locations[Bg3Constants.ScriptExtenderConfigLocationId].Should().Be(larian.Combine("ScriptExtender"));
    }

    [Fact]
    public void BaldursGate3_UnderProton_StillResolvesIntoThePrefix()
    {
        var prefix = ProtonTestPrefix.Create();

        var locations = Game.GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations[Bg3Constants.ModsLocationId].Should().Be(prefix.Home.Combine("AppData/Local/Larian Studios/Baldur's Gate 3/Mods"));
    }

    [Fact]
    public void OnTheHostTheKnownDataFolderIsXdgDataHome()
    {
        // BaldursGate3.GetLocations relies on this to put the native build's folders where the game reads them.
        FileSystem.Shared.GetKnownPath(KnownPath.LocalApplicationDataDirectory)
            .Should().Be(FileSystem.Shared.GetKnownPath(KnownPath.XDG_DATA_HOME));
    }

    [Fact]
    public void GetPrimaryFile_IsTheNativeExecutableOnLinuxAndTheLauncherOnWindows()
    {
        Game.GetPrimaryFile(InstallationOf(NativeResult())).Should().Be(new GamePath(LocationId.Game, "bin/bg3"));
        Game.GetPrimaryFile(InstallationOf(NativeResult(OSPlatform.Windows))).Should().Be(new GamePath(LocationId.Game, "Launcher/LariLauncher.exe"));
    }

    private static GameLocatorResult NativeResult(OSPlatform platform = default)
    {
        return new GameLocatorResult
        {
            Game = Substitute.For<IGameData>(),
            Path = HostFileSystem.FromUnsanitizedFullPath("/steam/steamapps/common/Baldurs Gate 3"),
            Store = GameStore.Steam,
            StoreIdentifier = "1086940",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
            Platform = platform == default ? OSPlatform.Linux : platform,
        };
    }

    private static GameInstallation InstallationOf(GameLocatorResult result)
        => new(result, GameLocations.Create(System.Collections.Immutable.ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, result.Path)));
}
