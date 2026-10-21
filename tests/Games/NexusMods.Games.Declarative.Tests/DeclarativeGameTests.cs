using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Declarative.Capabilities;
using NexusMods.Games.Declarative.Catalog;
using NexusMods.Games.Declarative.Schema;
using NexusMods.Games.Generic.Diagnostics;
using NexusMods.Games.Generic.Installers;
using NexusMods.Games.TestFramework.Helpers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Games.Declarative.Tests;

public class DeclarativeGameTests
{
    private const string Oblivion = """
        id: oblivion-like
        name: Oblivion Like
        nexus: { domain: oblivion, id: 101 }
        stores:
          steam: [22330]
          gog: [1458058109]
        primaryExe: Oblivion.exe
        nativeLinux: true
        locations:
          Game: { root: install }
          AppData: { root: "%LOCALAPPDATA%", path: Oblivion }
          Preferences: { root: "%MYGAMES%", path: Oblivion }
        """;

    private static string WithInstaller(string id) => $$"""
        id: {{id}}
        name: {{id}}
        stores: { steam: [7] }
        primaryExe: Game.exe
        locations:
          Game: { root: install }
        installer:
          dataMarkers: [meshes, textures]
          target: Game/Data
        """;

    private static ServiceProvider Build(IEnumerable<string> definitions, params IGameCapability[] capabilities)
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IFileSystem>(fileSystem)
            .AddSingleton(Substitute.For<IGameArtSource>())
            .AddSingleton<ProtonPrefixEmitter>();
        foreach (var capability in capabilities) services.AddSingleton(capability);
        services.AddDeclarativeGames(fileSystem, additionalBundled: definitions.Select((text, index) => new DefinitionSource($"test/{index}.yaml", text)));
        return services.BuildServiceProvider();
    }

    private static DeclarativeGame GameOf(string yaml, params IGameCapability[] capabilities)
        => Build([yaml], capabilities).GetServices<IGame>().Cast<DeclarativeGame>().Single();

    [Fact]
    public void Registration_AddsOneGameAndOneGameDataPerDefinition_AsTheSameInstances()
    {
        using var provider = Build([Samples.Minimal("game-a", 1), Samples.Minimal("game-b", 2)]);

        var games = provider.GetServices<IGame>().ToArray();
        var data = provider.GetServices<IGameData>().ToArray();

        games.Should().HaveCount(2);
        data.Should().HaveCount(2);
        provider.GetServices<IGame>().Cast<IGameData>().Should().Equal(data, ReferenceEquals);
        games.Select(g => g.GameId).Should().Equal(GameId.From("game-a"), GameId.From("game-b"));
    }

    [Fact]
    public void Registration_IgnoresADefinitionThatFailsValidation()
    {
        using var provider = Build([Samples.Minimal("game-a", 1), "id: Not A Valid Id"]);

        provider.GetServices<IGame>().Should().ContainSingle();
    }

    [Fact]
    public void Identity_ComesFromTheDefinition()
    {
        var game = GameOf(Oblivion);

        game.GameId.Should().Be(GameId.From("oblivion-like"));
        game.DisplayName.Should().Be("Oblivion Like");
        game.NexusModsGameId.Value.Value.Should().Be(101u);
        game.HasNativeLinuxBuild.Should().BeTrue();
        game.StoreIdentifiers.SteamAppIds.Should().Equal(22330u);
        game.StoreIdentifiers.GOGProductIds.Should().Equal(1458058109L);
        game.IconImage.Should().NotBeNull();
        game.TileImage.Should().NotBeNull();
    }

    [Fact]
    public void NoNexusBlock_MeansNoNexusModsGameId()
    {
        var game = GameOf(Samples.Minimal("no-nexus", 5));

        game.NexusModsGameId.HasValue.Should().BeFalse();
        game.HasNativeLinuxBuild.Should().BeFalse();
    }

    [Fact]
    public void Locations_ResolveTheTokensThroughTheGamesFileSystem()
    {
        var prefix = ProtonTestPrefix.Create();
        var game = GameOf(Oblivion);
        var located = prefix.LocatorResult();

        var locations = game.GetLocations(prefix.GameFileSystem, located);

        locations[LocationId.Game].Should().Be(located.Path);
        locations[LocationId.AppData].Should().Be(prefix.Home.Combine("AppData/Local/Oblivion"));
        locations[LocationId.Preferences].Should().Be(prefix.Home.Combine("Documents/My Games/Oblivion"));
    }

    [Fact]
    public void Locations_ResolveEveryRoot()
    {
        var prefix = ProtonTestPrefix.Create();
        var game = GameOf("""
            id: roots
            name: roots
            stores: { steam: [9] }
            primaryExe: Game.exe
            locations:
              Game: { root: install, path: bin }
              Roaming: { root: "%APPDATA%", path: Roots }
              Docs: { root: "%DOCUMENTS%" }
              Saved: { root: "%SAVEDGAMES%", path: Roots }
            """);
        var located = prefix.LocatorResult();

        var locations = game.GetLocations(prefix.GameFileSystem, located);

        locations[LocationId.Game].Should().Be(located.Path.Combine("bin"));
        locations[LocationId.From("Roaming")].Should().Be(prefix.Home.Combine("AppData/Roaming/Roots"));
        locations[LocationId.From("Docs")].Should().Be(prefix.Home.Combine("Documents"));
        locations[LocationId.From("Saved")].Should().Be(prefix.Home.Combine("Saved Games/Roots"));
    }

    [Fact]
    public void PrimaryFile_IsTheExecutableInTheGameFolder()
    {
        var game = GameOf(Oblivion);

        game.GetPrimaryFile(null!).Should().Be(new GamePath(LocationId.Game, "Oblivion.exe"));
    }

    [Fact]
    public void Installers_WithAnInstallerBlock_AreOneGenericPatternMatchInstaller()
    {
        var game = GameOf(WithInstaller("with-installer"));

        var installer = game.LibraryItemInstallers.Should().ContainSingle().Which.Should().BeOfType<GenericPatternMatchInstaller>().Subject;
        var target = installer.InstallFolderTargets.Should().ContainSingle().Subject;
        target.DestinationGamePath.Should().Be(new GamePath(LocationId.Game, "Data"));
        target.Names.Should().Equal(RelativePath.FromUnsanitizedInput("meshes"), RelativePath.FromUnsanitizedInput("textures"));
    }

    [Fact]
    public void Installers_AWholeLocationTargetHasAnEmptySubPath()
    {
        var game = GameOf(WithInstaller("whole").Replace("Game/Data", "Game"));

        var target = ((GenericPatternMatchInstaller)game.LibraryItemInstallers.Single()).InstallFolderTargets.Single();
        target.DestinationGamePath.Should().Be(new GamePath(LocationId.Game, ""));
    }

    [Fact]
    public void Installers_WithoutAnInstallerBlock_AreEmpty()
    {
        GameOf(Oblivion).LibraryItemInstallers.Should().BeEmpty();
    }

    [Fact]
    public void Installers_CapabilitiesComeBeforeTheInstallerBlock()
    {
        var capable = new PartsCapability("capable", new CapabilityParts { Installers = [Substitute.For<NexusMods.Abstractions.Library.Installers.ILibraryItemInstaller>()] });

        var game = GameOf(WithInstaller("ordered") + "\ncapabilities:\n  capable:\n", capable);

        game.LibraryItemInstallers.Should().HaveCount(2);
        game.LibraryItemInstallers[0].Should().NotBeOfType<GenericPatternMatchInstaller>();
        game.LibraryItemInstallers[1].Should().BeOfType<GenericPatternMatchInstaller>();
    }

    [Fact]
    public void Synchronizer_IsTheOneACapabilitySupplies()
    {
        var supplied = Substitute.For<ILoadoutSynchronizer>();
        var capable = new PartsCapability("capable", new CapabilityParts { SynchronizerFactory = _ => supplied });

        var game = GameOf(Samples.Minimal("supplied", 3, extra: "capabilities:\n  capable:\n"), capable);

        game.Synchronizer.Should().BeSameAs(supplied);
    }

    [Fact]
    public void Synchronizer_TwoCapabilitiesSupplyingOneIsAnError()
    {
        var one = new PartsCapability("one", new CapabilityParts { SynchronizerFactory = _ => Substitute.For<ILoadoutSynchronizer>() });
        var two = new PartsCapability("two", new CapabilityParts { SynchronizerFactory = _ => Substitute.For<ILoadoutSynchronizer>() });
        using var provider = Build([Samples.Minimal("both", 4, extra: "capabilities:\n  one:\n  two:\n")], one, two);

        var act = () => provider.GetServices<IGame>().ToArray();

        act.Should().Throw<DefinitionException>().WithMessage("*both*one*two*");
    }

    [Fact]
    public void Emitters_IncludeTheProtonPrefixEmitterUnlessProtonIsNone()
    {
        GameOf(Oblivion).DiagnosticEmitters.Should().ContainSingle(e => e is ProtonPrefixEmitter);
        GameOf(Samples.Minimal("no-proton", 6, extra: "runtime: { proton: none }")).DiagnosticEmitters.Should().NotContain(e => e is ProtonPrefixEmitter);
    }

    [Fact]
    public void Emitters_IncludeThoseOfCapabilities()
    {
        var emitter = Substitute.For<NexusMods.Abstractions.Diagnostics.Emitters.IDiagnosticEmitter>();
        var capable = new PartsCapability("capable", new CapabilityParts { Emitters = [emitter] });

        var game = GameOf(Samples.Minimal("with-emitter", 8, extra: "capabilities:\n  capable:\n"), capable);

        game.DiagnosticEmitters.Should().Contain(emitter);
    }

    [Fact]
    public void Locations_IncludeThoseCapabilitiesAdd()
    {
        var extra = ImmutableDictionary<LocationId, LocationDefinition>.Empty.Add(LocationId.From("Plugins"), new LocationDefinition { Root = LocationRoots.LocalAppData, Path = "Extra" });
        var capable = new PartsCapability("capable", new CapabilityParts { ExtraLocations = extra });
        var prefix = ProtonTestPrefix.Create();

        var game = GameOf(Samples.Minimal("extra-location", 10, extra: "capabilities:\n  capable:\n"), capable);

        game.GetLocations(prefix.GameFileSystem, prefix.LocatorResult())[LocationId.From("Plugins")].Should().Be(prefix.Home.Combine("AppData/Local/Extra"));
    }

    [Fact]
    public void FallbackCollectionDirectory_IsTheInstallerFallback()
    {
        var game = GameOf(WithInstaller("fallback") + "\n  fallback: Game/Data/Loose\n");

        game.GetFallbackCollectionInstallDirectory(null!).Value.Should().Be(new GamePath(LocationId.Game, "Data/Loose"));
        GameOf(Oblivion).GetFallbackCollectionInstallDirectory(null!).HasValue.Should().BeFalse();
    }

    [Fact]
    public void Catalog_ReplaysItsLoadProblemsIntoTheContainersLogger()
    {
        var logger = new RecordingLogger<GameDefinitionCatalog>();
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var services = new ServiceCollection()
            .AddSingleton<ILogger<GameDefinitionCatalog>>(logger)
            .AddSingleton<IFileSystem>(fileSystem)
            .AddDeclarativeGames(fileSystem, additionalBundled: [new DefinitionSource("test/broken.yaml", "id: Not A Valid Id")]);
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<GameDefinitionCatalog>();

        logger.Messages(LogLevel.Error).Should().ContainSingle(message => message.Contains("test/broken.yaml"));
    }
}

public sealed record EmptyConfig;

/// <summary>A capability that hands back fixed parts.</summary>
internal sealed class PartsCapability(string name, CapabilityParts parts) : IGameCapability
{
    public string Name => name;
    public Type ConfigType => typeof(EmptyConfig);
    public CapabilityParts Attach(GameDefinition definition, object config, IServiceProvider services) => parts;
}
