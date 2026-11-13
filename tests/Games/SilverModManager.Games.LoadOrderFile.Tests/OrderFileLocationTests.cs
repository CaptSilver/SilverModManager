using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using GameFinder.Wine;
using NexusMods.Paths;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class OrderFileLocationTests
{
    private const string Config = "Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml";

    private static readonly OrderFileLocation Location = new(ModLocationRoot.LocalAppDataLow, Config, NativeLinuxPath: "unity3d/" + Config);

    private static AbsolutePath TempFolder() => FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine("order-file-location-" + Guid.NewGuid().ToString("N"));

    private static GameLocatorResult Result(IFileSystem fileSystem, OSPlatform platform) => new()
    {
        Game = null!, Path = fileSystem.FromUnsanitizedFullPath("/tmp/game"), Store = GameStore.Steam, StoreIdentifier = "1",
        LocatorIds = ImmutableArray<LocatorId>.Empty, Locator = null!, Platform = platform,
    };

    [Fact]
    public void AGameFolderFileMapsToTheGameLocation()
    {
        OrderFileLocation.GameFolder("Mods/mod_order.txt").ToGamePath()
            .Should().Be(new GamePath(LocationId.Game, "Mods/mod_order.txt"));
    }

    [Fact]
    public void AKnownFolderFileMapsToItsOwnLocationByFileName()
    {
        Location.ToGamePath().Should().Be(new GamePath(SimpleLocations.LocalAppDataLow, "ModsConfig.xml"));
    }

    [Fact]
    public void AWindowsInstallResolvesTheFolderInsidethePrefix()
    {
        var overlay = new WinePrefix { ConfigurationDirectory = TempFolder() }.CreateOverlayFileSystem(FileSystem.Shared);

        Location.ResolveFolder(overlay, Result(overlay, OSPlatform.Windows))
            .Should().Be(overlay.GetKnownPath(KnownPath.LocalApplicationDataDirectory).Parent.Combine("LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Config"));
    }

    [Fact]
    public void ANativeInstallResolvesTheFolderBelowXdgConfig()
    {
        var xdg = FileSystem.Shared.CreateOverlayFileSystem(
            new Dictionary<AbsolutePath, AbsolutePath>(),
            new Dictionary<KnownPath, AbsolutePath> { [KnownPath.XDG_CONFIG_HOME] = TempFolder() });

        Location.ResolveFolder(xdg, Result(xdg, OSPlatform.Linux))
            .Should().Be(xdg.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config"));
    }

    [Fact]
    public void ALinuxInstallOfAGameWithoutANativePathStaysInThePrefix()
    {
        var overlay = new WinePrefix { ConfigurationDirectory = TempFolder() }.CreateOverlayFileSystem(FileSystem.Shared);
        var windowsOnly = Location with { NativeLinuxPath = null };

        windowsOnly.ResolveFolder(overlay, Result(overlay, OSPlatform.Linux))
            .Should().Be(overlay.GetKnownPath(KnownPath.LocalApplicationDataDirectory).Parent.Combine("LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Config"));
    }
}
