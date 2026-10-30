using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Backend.Games.Locators;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Tests.Games;

/// <summary>
/// <c>TryLocate</c> is what the manifest watcher calls on every Steam write while a game downloads, so it reads
/// the one appmanifest of the game it was asked about instead of every manifest of every library.
/// </summary>
public class SteamLocatorTryLocateTests
{
    private const uint AppId = 1091500;
    private const uint OtherAppId = 22330;
    private const uint ThirdAppId = 22320;

    [Fact]
    public void TryLocate_ParsesOnlyTheManifestOfTheGameAskedFor()
    {
        var fixture = new Fixture();
        fixture.AddGame(fixture.Steam, AppId);
        fixture.AddGame(fixture.Steam, OtherAppId);
        fixture.AddGame(fixture.Steam, ThirdAppId);
        var locator = fixture.Locator(AppId, OtherAppId, ThirdAppId);
        fixture.FileSystem.ResetReads();

        locator.TryLocate(fixture.Games[0], out var result).Should().BeTrue();

        result!.StoreIdentifier.Should().Be(AppId.ToString());
        fixture.FileSystem.ManifestReads.Should().ContainSingle().Which.Should().EndWith($"appmanifest_{AppId}.acf");
    }

    [Fact]
    public void TryLocate_GivesTheSameResultAsLocate()
    {
        var fixture = new Fixture();
        fixture.AddGame(fixture.Steam, AppId, stateFlags: "1026");
        fixture.AddGame(fixture.Steam, OtherAppId);
        var locator = fixture.Locator(AppId, OtherAppId);

        locator.TryLocate(fixture.Games[0], out var targeted).Should().BeTrue();

        var full = locator.Locate().Single(r => r.StoreIdentifier == AppId.ToString());
        targeted!.Game.Should().BeSameAs(full.Game);
        targeted.Path.GetFullPath().Should().Be(full.Path.GetFullPath());
        targeted.StoreBuildId.Should().Be(full.StoreBuildId);
        targeted.LocatorIds.Should().Equal(full.LocatorIds);
        targeted.StoreRoot.Should().Be(full.StoreRoot);
        targeted.StoreUpdateInProgress.Should().Be(full.StoreUpdateInProgress).And.BeTrue("state 1026 is an update running");
        targeted.StoreUpdatePending.Should().Be(full.StoreUpdatePending);
        targeted.Compatibility.Should().Be(full.Compatibility);
        targeted.Locator.Should().BeSameAs(locator);
    }

    [Fact]
    public void TryLocate_FindsAGameInAnotherLibrary()
    {
        var fixture = new Fixture();
        var second = fixture.FileSystem.FromUnsanitizedFullPath("/mnt/steam");
        fixture.AddLibrary(second);
        fixture.AddGame(fixture.Steam, OtherAppId);
        fixture.AddGame(second, AppId);
        var locator = fixture.Locator(AppId, OtherAppId);

        locator.TryLocate(fixture.Games[0], out var result).Should().BeTrue();

        result!.Path.GetFullPath().Should().StartWith("/mnt/steam/steamapps/common");
    }

    [Fact]
    public void TryLocate_AGameWithNoManifest_IsNotFound()
    {
        var fixture = new Fixture();
        fixture.AddGame(fixture.Steam, OtherAppId);
        var locator = fixture.Locator(AppId, OtherAppId);

        locator.TryLocate(fixture.Games[0], out var result).Should().BeFalse();

        result.Should().BeNull();
    }

    [Fact]
    public void TryLocate_AManifestSteamHasHalfWritten_IsNotFoundInsteadOfThrowing()
    {
        var fixture = new Fixture();
        fixture.AddGame(fixture.Steam, AppId);
        fixture.FileSystem.WriteAllText(fixture.Steam.Combine($"steamapps/appmanifest_{AppId}.acf"), "\"AppState\"\n{\n\"appid\" ");
        var locator = fixture.Locator(AppId);

        locator.TryLocate(fixture.Games[0], out _).Should().BeFalse();
    }

    [Fact]
    public void TryLocate_AGameWhoseIdAnotherGameRegisteredFirst_IsNotFound()
    {
        var fixture = new Fixture();
        fixture.AddGame(fixture.Steam, AppId);
        var first = Fixture.Game(1, AppId);
        var second = Fixture.Game(2, AppId);
        IGameLocator locator = new SteamLocator([first, second], NullLoggerFactory.Instance, fixture.FileSystem, registry: null);

        locator.TryLocate(first, out _).Should().BeTrue();
        locator.TryLocate(second, out _).Should().BeFalse("Locate gives the id to the first game registered for it");
    }

    [Fact]
    public void TryLocate_WithNoSteamInstalled_IsNotFound()
    {
        var fileSystem = new CountingFileSystem(OSInformation.FakeUnix);
        var game = Fixture.Game(1, AppId);
        IGameLocator locator = new SteamLocator([game], NullLoggerFactory.Instance, fileSystem, registry: null);

        locator.TryLocate(game, out _).Should().BeFalse();
    }

    private sealed class Fixture
    {
        public CountingFileSystem FileSystem { get; } = new(OSInformation.FakeUnix);
        public AbsolutePath Steam { get; }
        public List<IGameData> Games { get; } = [];
        private readonly List<AbsolutePath> _libraries = [];

        public Fixture()
        {
            Steam = FileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam");
            Write(Steam.Combine("steam.sh"), "");
            _libraries.Add(Steam);
            WriteLibraryFolders();
        }

        public void AddLibrary(AbsolutePath library)
        {
            _libraries.Add(library);
            WriteLibraryFolders();
        }

        public void AddGame(AbsolutePath library, uint appId, string stateFlags = "4")
        {
            Write(library.Combine($"steamapps/appmanifest_{appId}.acf"), Manifest(appId, stateFlags));
            Write(library.Combine($"steamapps/common/Game{appId}/game.exe"), "");
        }

        public IGameLocator Locator(params uint[] appIds)
        {
            Games.Clear();
            Games.AddRange(appIds.Select((id, index) => Game((uint)index + 1, id)));
            return new SteamLocator(Games, NullLoggerFactory.Instance, FileSystem, registry: null);
        }

        public static IGameData Game(uint gameId, uint appId)
        {
            var game = Substitute.For<SubstitutableGameData>();
            game.GameId = GameId.From(gameId);
            game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(gameId)) { SteamAppIds = [appId] });
            return game;
        }

        private void WriteLibraryFolders()
        {
            var text = "\"libraryfolders\"\n{\n" + string.Join("\n", _libraries.Select((library, index) =>
                $"\"{index}\"\n{{\n\"path\" \"{library}\"\n\"label\" \"\"\n\"contentid\" \"{index + 1}\"\n\"totalsize\" \"0\"\n\"apps\"\n{{\n}}\n}}")) + "\n}\n";
            Write(Steam.Combine("config/libraryfolders.vdf"), text);
            Write(Steam.Combine("steamapps/libraryfolders.vdf"), text);
        }

        private void Write(AbsolutePath path, string contents)
        {
            FileSystem.CreateDirectory(path.Parent);
            FileSystem.WriteAllText(path, contents);
        }

        private static string Manifest(uint appId, string stateFlags) => $$"""
            "AppState"
            {
                "appid" "{{appId}}"
                "Universe" "1"
                "name" "Test Game"
                "StateFlags" "{{stateFlags}}"
                "installdir" "Game{{appId}}"
                "LastOwner" "76561198000000000"
                "BuildID" "1"
                "InstalledDepots"
                {
                    "{{appId + 1}}"
                    {
                        "manifest" "123456789"
                        "size" "1"
                    }
                }
            }
            """;
    }

    /// <summary><see cref="IGameData"/> with a plain <see cref="GameId"/>: NSubstitute builds a default value for an unconfigured property first, and a game id has no default.</summary>
    public abstract class SubstitutableGameData : IGameData
    {
        public GameId GameId { get; set; }
        public abstract StoreIdentifiers StoreIdentifiers { get; }
        public abstract string DisplayName { get; }
        public abstract DynamicData.Kernel.Optional<SilverModManager.Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId { get; }
        public abstract SilverModManager.Sdk.IO.IStreamFactory IconImage { get; }
        public abstract SilverModManager.Sdk.IO.IStreamFactory TileImage { get; }
        public abstract System.Collections.Immutable.ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult);
        public abstract GamePath GetPrimaryFile(GameInstallation installation);
    }

    /// <summary>An in-memory file system that remembers which appmanifest files were opened for reading.</summary>
    private sealed class CountingFileSystem(IOSInformation os) : InMemoryFileSystem(os), IFileSystem
    {
        public ConcurrentBag<string> ManifestReads { get; private set; } = [];

        public void ResetReads() => ManifestReads = [];

        private void Note(AbsolutePath path)
        {
            if (path.FileName.StartsWith("appmanifest_", StringComparison.Ordinal)) ManifestReads.Add(path.GetFullPath());
        }

        Stream IFileSystem.ReadFile(AbsolutePath path) { Note(path); return ReadFile(path); }
        Stream IFileSystem.OpenFile(AbsolutePath path, FileMode mode, FileAccess access, FileShare share)
        {
            if (access.HasFlag(FileAccess.Read)) Note(path);
            return OpenFile(path, mode, access, share);
        }
    }
}
