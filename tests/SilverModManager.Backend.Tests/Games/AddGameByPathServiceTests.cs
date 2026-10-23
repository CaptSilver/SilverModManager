using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Backend.Games.UserGames;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Backend.Tests.Games;

public class AddGameByPathServiceTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly RecordingStore _store = new();
    private readonly IGameRegistry _registry = Substitute.For<IGameRegistry>();
    private readonly List<IGameData> _games = [];

    private sealed class RecordingStore : IUserGameInstallStore
    {
        public List<UserGameInstall> Installs { get; } = [];
        public string? RefuseWith { get; set; }

        public IReadOnlyList<UserGameInstall> GetAll() => Installs;

        public void Add(UserGameInstall install)
        {
            if (RefuseWith is not null) throw new InvalidOperationException(RefuseWith);
            Installs.Add(install);
        }
    }

    private AddGameByPathService Service()
    {
        _registry.LocateGameInstallations().Returns(_ => ImmutableArray<GameInstallation>.Empty);
        return new AddGameByPathService(_games, _registry, _store, _fs, NullLoggerFactory.Instance);
    }

    private AbsolutePath Folder(string path, params string[] files)
    {
        var folder = _fs.FromUnsanitizedFullPath(path);
        folder.CreateDirectory();
        foreach (var file in files)
        {
            var target = folder.Combine(file);
            target.Parent.CreateDirectory();
            _fs.WriteAllText(target, "");
        }
        return folder;
    }

    private IGameData Game(string id, string exe, bool native = false, uint[]? steam = null, long[]? gog = null)
    {
        var game = new FakeGameData(id, native, exe)
        {
            StoreIdentifiers = new StoreIdentifiers(GameId.From(id))
            {
                SteamAppIds = steam is null ? [] : [..steam],
                GOGProductIds = gog is null ? [] : [..gog],
            },
        };
        _games.Add(game);
        return game;
    }

    private void Detected(IGameData game, GameStore store, AbsolutePath path)
    {
        var result = new GameLocatorResult
        {
            Game = game,
            Path = path,
            Store = store,
            StoreIdentifier = "x",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
        };
        _registry.LocateGameInstallations().Returns(_ =>
            [new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, path)))]);
    }

    [Fact]
    public void AWindowsGameWithAPrefixIsRecordedAsWindows()
    {
        var game = Game("alpha", "Alpha.exe");
        var folder = Folder("/games/alpha", "Alpha.exe");
        var prefix = _fs.FromUnsanitizedFullPath("/prefixes/alpha");

        var result = Service().Add(folder, null, prefix, null);

        result.Should().Be(new AddGameResult.Added(game));
        _store.Installs.Should().Equal(new UserGameInstall(game.GameId, "/games/alpha", OSPlatform.Windows, "/prefixes/alpha"));
        _registry.Received(1).ClearCache();
    }

    [Fact]
    public void AWindowsGameWithoutAPrefixNeedsOneAndWritesNothing()
    {
        var game = Game("alpha", "Alpha.exe");

        var result = Service().Add(Folder("/games/alpha", "Alpha.exe"), null, null, null);

        result.Should().Be(new AddGameResult.NeedsPrefix(game));
        _store.Installs.Should().BeEmpty();
        _registry.DidNotReceive().ClearCache();
    }

    [Fact]
    public void ANativeGameIsRecordedAsLinuxWithoutAPrefix()
    {
        var game = Game("alpha", "Alpha.x86_64", native: true);

        var result = Service().Add(Folder("/games/alpha", "Alpha.x86_64"), null, null, null);

        result.Should().Be(new AddGameResult.Added(game));
        _store.Installs.Should().Equal(new UserGameInstall(game.GameId, "/games/alpha", OSPlatform.Linux));
    }

    [Fact]
    public void TheWineBinaryIsRecorded()
    {
        var game = Game("alpha", "Alpha.exe");
        var prefix = _fs.FromUnsanitizedFullPath("/prefixes/alpha");
        var wine = _fs.FromUnsanitizedFullPath("/opt/wine/bin/wine");

        Service().Add(Folder("/games/alpha", "Alpha.exe"), null, prefix, wine);

        _store.Installs.Should().Equal(new UserGameInstall(game.GameId, "/games/alpha", OSPlatform.Windows, "/prefixes/alpha", "/opt/wine/bin/wine"));
    }

    [Fact]
    public void TheExecutableIsMatchedIgnoringCase()
    {
        var game = Game("alpha", "Alpha.exe");
        var prefix = _fs.FromUnsanitizedFullPath("/prefixes/alpha");

        var result = Service().Add(Folder("/games/alpha", "ALPHA.EXE"), null, prefix, null);

        result.Should().Be(new AddGameResult.Added(game));
    }

    [Fact]
    public void AnExecutableBelowTheRootIsMatchedIgnoringCaseInEveryFolder()
    {
        var game = Game("alpha", "bin/x64/Alpha.exe");
        var prefix = _fs.FromUnsanitizedFullPath("/prefixes/alpha");

        var result = Service().Add(Folder("/games/alpha", "BIN/x64/alpha.exe"), null, prefix, null);

        result.Should().Be(new AddGameResult.Added(game));
    }

    [Fact]
    public void AnExecutableInASubfolderDoesNotMatchTheFolder()
    {
        Game("alpha", "Alpha.exe");

        var result = Service().Add(Folder("/games/alpha", "Backup/Alpha.exe"), null, null, null);

        result.Should().Be(new AddGameResult.Unknown());
    }

    [Fact]
    public void TwoGamesWithOneExecutableAreAmbiguousAndNothingIsWritten()
    {
        var first = Game("first", "Shared.exe");
        var second = Game("second", "Shared.exe");

        var result = Service().Add(Folder("/games/shared", "Shared.exe"), null, null, null);

        var ambiguous = result.Should().BeOfType<AddGameResult.Ambiguous>().Subject;
        ambiguous.Candidates.Select(candidate => candidate.Game).Should().Equal(first, second);
        ambiguous.Candidates.Select(candidate => candidate.File.ToString()).Should().Equal("Shared.exe", "Shared.exe");
        _store.Installs.Should().BeEmpty();
    }

    [Fact]
    public void EachAmbiguousCandidateCarriesItsOwnExecutable()
    {
        var first = Game("first", "First.exe");
        var second = Game("second", "Second.exe");

        var result = Service().Add(Folder("/games/both", "First.exe", "Second.exe"), null, null, null);

        var ambiguous = result.Should().BeOfType<AddGameResult.Ambiguous>().Subject;
        ambiguous.Candidates.Select(candidate => (candidate.Game, candidate.File.ToString()))
            .Should().Equal((first, "First.exe"), (second, "Second.exe"));
    }

    [Fact]
    public void IdentifyingFilesPickTheGameThatHasThem()
    {
        Game("plain", "Shared.exe");
        var special = new FakeIdentifiedGameData("special", "Shared.exe", ["Data/Special.esm"]);
        _games.Add(special);
        var prefix = _fs.FromUnsanitizedFullPath("/prefixes/x");

        var result = Service().Add(Folder("/games/shared", "Shared.exe", "data/special.esm"), null, prefix, null);

        result.Should().Be(new AddGameResult.Added(special));
    }

    [Fact]
    public void MissingIdentifyingFilesPickTheGameThatDeclaresNone()
    {
        var plain = Game("plain", "Shared.exe");
        _games.Add(new FakeIdentifiedGameData("special", "Shared.exe", ["Special.esm"]));
        var prefix = _fs.FromUnsanitizedFullPath("/prefixes/x");

        var result = Service().Add(Folder("/games/shared", "Shared.exe"), null, prefix, null);

        result.Should().Be(new AddGameResult.Added(plain));
    }

    [Fact]
    public void IdentifyingFilesMustAllBePresent()
    {
        var plain = Game("plain", "Shared.exe");
        _games.Add(new FakeIdentifiedGameData("special", "Shared.exe", ["Launcher.exe", "Special.esm"]));
        var prefix = _fs.FromUnsanitizedFullPath("/prefixes/x");

        var result = Service().Add(Folder("/games/shared", "Shared.exe", "Launcher.exe"), null, prefix, null);

        result.Should().Be(new AddGameResult.Added(plain));
    }

    [Fact]
    public void AGameIdArgumentPicksBetweenCandidates()
    {
        Game("first", "Shared.exe");
        var second = Game("second", "Shared.exe");
        var prefix = _fs.FromUnsanitizedFullPath("/prefixes/x");

        var result = Service().Add(Folder("/games/shared", "Shared.exe"), second.GameId, prefix, null);

        result.Should().Be(new AddGameResult.Added(second));
    }

    [Fact]
    public void AGameIdArgumentThatDoesNotLaunchFromTheFolderIsUnknown()
    {
        Game("first", "First.exe");
        var second = Game("second", "Second.exe");

        var result = Service().Add(Folder("/games/first", "First.exe"), second.GameId, null, null);

        result.Should().Be(new AddGameResult.Unknown());
        _store.Installs.Should().BeEmpty();
    }

    [Fact]
    public void ASteamManifestForTheFolderPicksItsGame()
    {
        Game("first", "Shared.exe", steam: [111u]);
        var second = Game("second", "Shared.exe", steam: [222u]);
        Folder("/steam/steamapps/common/Shared Game", "Shared.exe");
        _fs.WriteAllText(_fs.FromUnsanitizedFullPath("/steam/steamapps/appmanifest_222.acf"), Manifest(222, "Shared Game"));
        _fs.WriteAllText(_fs.FromUnsanitizedFullPath("/steam/steamapps/appmanifest_111.acf"), Manifest(111, "Another Folder"));
        var prefix = _fs.FromUnsanitizedFullPath("/prefixes/x");

        var result = Service().Add(_fs.FromUnsanitizedFullPath("/steam/steamapps/common/Shared Game"), null, prefix, null);

        result.Should().Be(new AddGameResult.Added(second));
    }

    [Fact]
    public void AGogInfoFileForTheFolderPicksItsGame()
    {
        Game("first", "Shared.exe", gog: [111]);
        var second = Game("second", "Shared.exe", gog: [222]);
        var prefix = _fs.FromUnsanitizedFullPath("/prefixes/x");

        var result = Service().Add(Folder("/games/shared", "Shared.exe", "goggame-222.info"), null, prefix, null);

        result.Should().Be(new AddGameResult.Added(second));
    }

    [Fact]
    public void AGogInfoFileInASubfolderDecidesNothing()
    {
        Game("first", "Shared.exe", gog: [111]);
        Game("second", "Shared.exe", gog: [222]);

        var result = Service().Add(Folder("/games/shared", "Shared.exe", "Extras/goggame-222.info"), null, null, null);

        result.Should().BeOfType<AddGameResult.Ambiguous>();
    }

    [Fact]
    public void AFolderTheRegistryAlreadyFoundIsAlreadyDetected()
    {
        var game = Game("alpha", "Alpha.exe");
        var folder = Folder("/games/alpha", "Alpha.exe");
        var service = Service();
        Detected(game, GameStore.Steam, folder);

        var result = service.Add(folder, null, null, null);

        result.Should().Be(new AddGameResult.AlreadyDetected(game, GameStore.Steam));
        _store.Installs.Should().BeEmpty();
    }

    [Fact]
    public void AnAlreadyDetectedFolderIsReportedBeforeAmbiguity()
    {
        var first = Game("first", "Shared.exe");
        Game("second", "Shared.exe");
        var folder = Folder("/games/shared", "Shared.exe");
        var service = Service();
        Detected(first, GameStore.Steam, folder);

        service.Add(folder, null, null, null).Should().Be(new AddGameResult.AlreadyDetected(first, GameStore.Steam));
    }

    // Whether two spellings of one folder (/home vs /var/home) count as the same is InstallPaths.AreSameFolder's
    // job, covered by InstallPathsTests: InMemoryFileSystem can't hold a symlink. This shows the service asks
    // the registry, ManuallyAdded results included, and that a folder under another spelling is not taken for it.
    [Fact]
    public void AddingTheSameFolderTwiceIsRefusedTheSecondTime()
    {
        var game = Game("alpha", "Alpha.exe");
        var folder = Folder("/games/alpha", "Alpha.exe");
        var prefix = _fs.FromUnsanitizedFullPath("/prefixes/alpha");
        var service = Service();
        service.Add(folder, null, prefix, null).Should().Be(new AddGameResult.Added(game));
        Detected(game, GameStore.ManuallyAdded, folder);

        service.Add(folder, null, prefix, null).Should().Be(new AddGameResult.AlreadyDetected(game, GameStore.ManuallyAdded));
        _store.Installs.Should().HaveCount(1);
    }

    [Fact]
    public void ADetectedFolderUnderAnotherSpellingIsNotTheSameFolder()
    {
        var game = Game("alpha", "Alpha.exe");
        var folder = Folder("/games/alpha", "Alpha.exe");
        var prefix = _fs.FromUnsanitizedFullPath("/prefixes/alpha");
        var service = Service();
        Detected(game, GameStore.Steam, _fs.FromUnsanitizedFullPath("/games/Alpha"));

        service.Add(folder, null, prefix, null).Should().Be(new AddGameResult.Added(game));
    }

    [Fact]
    public void AStoreThatRefusesTheRecordIsReportedWithItsMessage()
    {
        Game("alpha", "Alpha.exe", native: true);
        _store.RefuseWith = "installs.yaml could not be parsed";

        var result = Service().Add(Folder("/games/alpha", "Alpha.exe"), null, null, null);

        result.Should().Be(new AddGameResult.StoreUnavailable("installs.yaml could not be parsed"));
        _registry.DidNotReceive().ClearCache();
    }

    [Fact]
    public void AFolderNoGameLaunchesFromIsUnknown()
    {
        Game("alpha", "Alpha.exe");

        var result = Service().Add(Folder("/games/other", "Other.exe"), null, null, null);

        result.Should().Be(new AddGameResult.Unknown());
        _store.Installs.Should().BeEmpty();
    }

    private static string Manifest(uint appId, string installDir) => $$"""
        "AppState"
        {
            "appid"		"{{appId}}"
            "Universe"		"1"
            "name"		"{{installDir}}"
            "StateFlags"		"4"
            "installdir"		"{{installDir}}"
            "LastUpdated"		"1700000000"
            "SizeOnDisk"		"1"
            "buildid"		"1"
            "LastOwner"		"1"
        }
        """;
}
