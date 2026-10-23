using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NexusMods.Backend.Games.Locators;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Backend.Tests.Games;

public sealed class LutrisLocatorTests : IDisposable
{
    // The games table is the one `sqlite3 ~/.local/share/lutris/pga.db '.schema games'` prints on a Lutris
    // install:
    //   CREATE TABLE games (id INTEGER PRIMARY KEY, name TEXT, sortname TEXT, slug TEXT, installer_slug TEXT,
    //   parent_slug TEXT, platform TEXT, runner TEXT, executable TEXT, directory TEXT, updated DATETIME,
    //   lastplayed INTEGER, installed INTEGER, installed_at INTEGER, year INTEGER, configpath TEXT,
    //   has_custom_banner INTEGER, has_custom_icon INTEGER, has_custom_coverart_big INTEGER, playtime REAL,
    //   service TEXT, service_id TEXT, discord_id TEXT);
    // The reader only selects slug, runner, directory, installed, configpath, service and service_id, so the
    // fixture creates just those. Per-game settings live in <config>/games/<configpath>.yml with the game's
    // exe, prefix and working_dir under "game:" and the Wine build under "wine: version:".
    private const long GogId = 1207658924;
    private const string EpicId = "0123456789abcdef0123456789abcdef";

    private readonly string _hostDir = Path.Combine(Path.GetTempPath(), "lutris-locator-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryFileSystem _fileSystem = new(OSInformation.FakeUnix);

    public void Dispose()
    {
        try { Directory.Delete(_hostDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Locate_GogRow_FindsTheGameWithAPlainWineLayer()
    {
        var root = NativeRoot();
        var home = Home();
        var wine = root.DataDir.Combine("runners/wine/lutris-ge-8-26/bin/wine");
        _fileSystem.CreateDirectory(wine.Parent);
        _fileSystem.WriteAllText(wine, "");
        WriteGame(root, "game-slug", "gog", GogId.ToString(), runner: "wine", directory: home.Combine("Games/game").GetFullPath(), exe: "Game.exe", prefix: home.Combine("Games/prefix").GetFullPath(), wineVersion: "lutris-ge-8-26");

        var result = Locate(root, Game("Game", gogId: GogId));

        result.Store.Should().Be(GameStore.GOG);
        result.StoreIdentifier.Should().Be(GogId.ToString());
        result.LocatorIds.Should().BeEmpty();
        result.Path.Should().Be(home.Combine("Games/game"));
        result.Platform.Should().Be(OSPlatform.Windows);
        result.WinePrefixPath.Should().Be(home.Combine("Games/prefix"));
        result.Compatibility.Should().Be(new CompatibilityLayer(CompatibilityLayerKind.PlainWine, home.Combine("Games/prefix"), wine, "lutris:lutris-ge-8-26"));
    }

    [Fact]
    public void Locate_EpicRow_FindsTheGameAsEpic()
    {
        var root = NativeRoot();
        var home = Home();
        WriteGame(root, "epic-game", "egs", EpicId, runner: "wine", directory: home.Combine("Games/epic").GetFullPath(), exe: "Game.exe", prefix: home.Combine("Games/epic-prefix").GetFullPath(), wineVersion: "lutris-7.2");

        var result = Locate(root, Game("Game", epicId: EpicId));

        result.Store.Should().Be(GameStore.EGS);
        result.StoreIdentifier.Should().Be(EpicId);
        result.Compatibility!.Kind.Should().Be(CompatibilityLayerKind.PlainWine);
        result.Compatibility.WineBinary.Should().BeNull("the runner folder does not exist, so there is no wine binary to name");
        result.Compatibility.RunnerName.Should().Be("lutris:lutris-7.2");
    }

    [Fact]
    public void Locate_WineGameWithoutAService_MatchesByTheExecutableName()
    {
        var root = NativeRoot();
        var home = Home();
        WriteGame(root, "plain", service: null, serviceId: null, runner: "wine", directory: home.Combine("Games/plain").GetFullPath(), exe: "C:\\Games\\Plain\\Game.EXE", prefix: home.Combine("p").GetFullPath(), wineVersion: "system");
        var game = Game("Game", primaryFile: "game.exe");

        var result = Locate(root, game);

        result.Store.Should().Be(GameStore.Lutris);
        result.Game.Should().BeSameAs(game);
        result.StoreIdentifier.Should().Be("plain");
        result.Path.Should().Be(home.Combine("Games/plain"));
    }

    [Fact]
    public void Locate_WineGameWithoutADirectory_UsesTheExecutablesFolder()
    {
        var root = NativeRoot();
        var home = Home();
        WriteGame(root, "plain", service: null, serviceId: null, runner: "wine", directory: null, exe: home.Combine("Games/plain/Game.exe").GetFullPath(), prefix: home.Combine("p").GetFullPath());

        Locate(root, Game("Game", primaryFile: "Game.exe")).Path.Should().Be(home.Combine("Games/plain"));
    }

    [Fact]
    public void Locate_PrimaryFileInASubfolder_PathIsTheFolderHoldingIt()
    {
        var root = NativeRoot();
        var home = Home();
        var exe = home.Combine("Games/deep/bin/x64/Game.exe");
        _fileSystem.CreateDirectory(exe.Parent);
        _fileSystem.WriteAllText(exe, "");
        WriteGame(root, "deep", service: null, serviceId: null, runner: "wine", directory: null, exe: exe.GetFullPath(), prefix: home.Combine("p").GetFullPath());

        Locate(root, Game("Game", primaryFile: "bin/x64/Game.exe")).Path.Should().Be(home.Combine("Games/deep"));
    }

    [Fact]
    public void Locate_DirectoryIsThePrefix_PathIsTheGameInsideDriveC()
    {
        // Lutris's own installers set prefix to $GAMEDIR and record the exe relative to it, so the directory
        // column names the prefix rather than the game.
        var root = NativeRoot();
        var home = Home();
        var prefix = home.Combine("Games/gog/game");
        var game = prefix.Combine("drive_c/GOG Games/Game");
        _fileSystem.CreateDirectory(game.Combine("Launcher"));
        _fileSystem.WriteAllText(game.Combine("Launcher/Game.exe"), "");
        _fileSystem.CreateDirectory(game.Combine("bin"));
        _fileSystem.WriteAllText(game.Combine("bin/real.exe"), "");
        WriteGame(root, "game", "gog", GogId.ToString(), runner: "wine", directory: prefix.GetFullPath(), exe: "drive_c/GOG Games/Game/bin/real.exe", prefix: prefix.GetFullPath());

        Locate(root, Game("Game", gogId: GogId, primaryFile: "Launcher/Game.exe")).Path.Should().Be(game);
    }

    [Fact]
    public void Locate_WindowsExePathAndDirectoryIsThePrefix_ResolvesTheExeThroughDriveC()
    {
        var root = NativeRoot();
        var home = Home();
        var prefix = home.Combine("Games/epic");
        WriteGame(root, "epic-game", "egs", EpicId, runner: "wine", directory: prefix.GetFullPath(), exe: "C:\\Games\\Epic Game\\Game.exe", prefix: prefix.GetFullPath());

        Locate(root, Game("Game", epicId: EpicId)).Path.Should().Be(prefix.Combine("drive_c/Games/Epic Game"));
    }

    [Fact]
    public void Locate_ExecutableNoGameClaims_IsIgnored()
    {
        var root = NativeRoot();
        WriteGame(root, "other", service: null, serviceId: null, runner: "wine", directory: "/games/other", exe: "Other.exe", prefix: "/p");

        NewLocator(root, Game("Game", primaryFile: "Game.exe")).Locate().Should().BeEmpty();
    }

    [Fact]
    public void Locate_NotInstalledRow_IsIgnored()
    {
        var root = NativeRoot();
        WriteGame(root, "gone", "gog", GogId.ToString(), runner: "wine", directory: "/games/gone", exe: "Game.exe", prefix: "/p", installed: 0);

        NewLocator(root, Game("Game", gogId: GogId)).Locate().Should().BeEmpty();
    }

    [Fact]
    public void Locate_RowWithoutItsYaml_IsSkippedWithAWarning()
    {
        var root = NativeRoot();
        WriteGame(root, "no-yaml", "gog", GogId.ToString(), runner: "wine", directory: "/games/x", exe: "Game.exe", prefix: "/p", writeYaml: false);
        var logger = new RecordingLogger<LutrisLocator>();

        NewLocator(root, logger, Game("Game", gogId: GogId)).Locate().Should().BeEmpty();

        logger.Count(LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public void Locate_NativeLinuxRunner_HasNoCompatibilityLayer()
    {
        var root = NativeRoot();
        WriteGame(root, "native", "gog", GogId.ToString(), runner: "linux", directory: "/games/native", exe: "start.sh", prefix: null);

        var result = Locate(root, Game("Game", gogId: GogId));

        result.Platform.Should().Be(OSPlatform.Linux);
        result.Compatibility.Should().BeNull();
    }

    [Fact]
    public void Locate_FlatpakLutris_IsReadAsASecondRoot()
    {
        var native = NativeRoot();
        var flatpak = FlatpakRoot();
        // The native root has no pga.db at all.
        WriteGame(flatpak, "flatpak-game", "gog", GogId.ToString(), runner: "wine", directory: "/games/f", exe: "Game.exe", prefix: "/pf");

        Locate([native, flatpak], Game("Game", gogId: GogId)).Path.Should().Be(_fileSystem.FromUnsanitizedFullPath("/games/f"));
    }

    [Fact]
    public void Locate_SameSlugInBothRoots_YieldsNothingAndRecordsAProblemNamingBoth()
    {
        var native = NativeRoot();
        var flatpak = FlatpakRoot();
        WriteGame(native, "twice", "gog", GogId.ToString(), runner: "wine", directory: "/games/native", exe: "Game.exe", prefix: "/p");
        WriteGame(flatpak, "twice", "gog", GogId.ToString(), runner: "wine", directory: "/games/flatpak", exe: "Game.exe", prefix: "/p");
        var problems = new CollectingProblemSink();

        NewLocator([native, flatpak], new RecordingLogger<LutrisLocator>(), Game("Game", gogId: GogId)).Locate(problems).Should().BeEmpty();

        problems.Problems.Should().ContainSingle().Which.Message.Should().Contain("/games/native").And.Contain("/games/flatpak");
    }

    [Fact]
    public void Locate_SameSlugInBothRootsForAGameNobodyClaims_IsNotAProblem()
    {
        var native = NativeRoot();
        var flatpak = FlatpakRoot();
        WriteGame(native, "twice", "gog", "42", runner: "wine", directory: "/games/native", exe: "Game.exe", prefix: "/p");
        WriteGame(flatpak, "twice", "gog", "42", runner: "wine", directory: "/games/flatpak", exe: "Game.exe", prefix: "/p");
        var problems = new CollectingProblemSink();

        NewLocator([native, flatpak], new RecordingLogger<LutrisLocator>(), Game("Game", gogId: GogId)).Locate(problems).Should().BeEmpty();

        problems.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Locate_UnreadableDatabaseInOneRoot_StillLocatesTheOtherRootsGames()
    {
        var native = NativeRoot();
        var flatpak = FlatpakRoot();
        // A pga.db that is not a database, as a half-synced or unmounted drive can leave behind.
        Directory.CreateDirectory(DataDir(native));
        File.WriteAllText(Path.Combine(DataDir(native), "pga.db"), "this is not sqlite");
        WriteGame(flatpak, "ok", "gog", GogId.ToString(), runner: "wine", directory: "/games/ok", exe: "Game.exe", prefix: "/p");
        var logger = new RecordingLogger<LutrisLocator>();

        NewLocator([native, flatpak], logger, Game("Game", gogId: GogId)).Locate().Should().ContainSingle();

        logger.Count(LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public void Locate_YamlThatCannotBeParsed_SkipsThatGameOnly()
    {
        var root = NativeRoot();
        WriteGame(root, "broken", "gog", GogId.ToString(), runner: "wine", directory: "/games/broken", exe: "Game.exe", prefix: "/p", yaml: "game: [unterminated");
        WriteGame(root, "fine", "egs", EpicId, runner: "wine", directory: "/games/fine", exe: "Game.exe", prefix: "/p");
        var logger = new RecordingLogger<LutrisLocator>();

        NewLocator(root, logger, Game("Game", gogId: GogId, epicId: EpicId)).Locate().Should().ContainSingle().Which.Store.Should().Be(GameStore.EGS);

        logger.Count(LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public void Locate_NoLutrisInstalled_FindsNothing()
    {
        NewLocator(NativeRoot(), Game("Game", gogId: GogId)).Locate().Should().BeEmpty();
    }

    [Fact]
    public void Locate_GamesSharingAnExecutableNameAndNoRows_ReportsNoProblem()
    {
        var problems = new CollectingProblemSink();

        NewLocator(NativeRoot(), SharedSkyrim(), SharedEnderal()).Locate(problems).Should().BeEmpty();

        problems.Problems.Should().BeEmpty("a shared executable is told apart by identifying files, it is not a clash");
    }

    [Fact]
    public void Locate_SharedExecutableAndTheFolderHoldsTheIdentifyingFiles_LocatesTheGameThatNamesThem()
    {
        var root = NativeRoot();
        var folder = Home().Combine("Games/enderal");
        _fileSystem.CreateDirectory(folder.Combine("Data"));
        _fileSystem.WriteAllText(folder.Combine("TESV.exe"), "");
        _fileSystem.WriteAllText(folder.Combine("Enderal Launcher.exe"), "");
        _fileSystem.WriteAllText(folder.Combine("Data/Enderal - Forgotten Stories.esm"), "");
        WriteGame(root, "enderal", service: null, serviceId: null, runner: "wine", directory: folder.GetFullPath(), exe: "TESV.exe", prefix: Home().Combine("p").GetFullPath());
        var enderal = SharedEnderal();
        var problems = new CollectingProblemSink();

        var result = NewLocator(root, SharedSkyrim(), enderal).Locate(problems).Should().ContainSingle().Subject;

        result.Game.Should().BeSameAs(enderal);
        result.Path.Should().Be(folder);
        problems.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Locate_SharedExecutableAndTheFolderLacksTheIdentifyingFiles_LocatesTheGameThatNamesNone()
    {
        var root = NativeRoot();
        var folder = Home().Combine("Games/skyrim");
        _fileSystem.CreateDirectory(folder);
        _fileSystem.WriteAllText(folder.Combine("TESV.exe"), "");
        WriteGame(root, "skyrim", service: null, serviceId: null, runner: "wine", directory: folder.GetFullPath(), exe: "TESV.exe", prefix: Home().Combine("p").GetFullPath());
        var skyrim = SharedSkyrim();

        var result = NewLocator(root, skyrim, SharedEnderal()).Locate(new CollectingProblemSink()).Should().ContainSingle().Subject;

        result.Game.Should().BeSameAs(skyrim);
    }

    [Fact]
    public void Locate_SharedExecutableAndNothingTellsTheGamesApart_LocatesNothingAndNamesTheCandidates()
    {
        var root = NativeRoot();
        var folder = Home().Combine("Games/shared");
        _fileSystem.CreateDirectory(folder);
        _fileSystem.WriteAllText(folder.Combine("Shared.exe"), "");
        WriteGame(root, "shared-row", service: null, serviceId: null, runner: "wine", directory: folder.GetFullPath(), exe: "Shared.exe", prefix: Home().Combine("p").GetFullPath());
        var problems = new CollectingProblemSink();

        NewLocator(root, new FakeGameData("Alpha", primaryFile: "Shared.exe"), new FakeGameData("Beta", primaryFile: "Shared.exe"))
            .Locate(problems).Should().BeEmpty();

        var problem = problems.Problems.Should().ContainSingle().Subject;
        problem.Locator.Should().Be(nameof(LutrisLocator));
        problem.Message.Should().Contain("shared-row").And.Contain("Shared.exe").And.Contain("Alpha").And.Contain("Beta").And.Contain("Add a game folder");
    }

    [Fact]
    public void Locate_SharedExecutableRowWithUnusablePaths_SkipsThatRowAndLocatesTheRest()
    {
        var root = NativeRoot();
        WriteGame(root, "broken", service: null, serviceId: null, runner: "wine", directory: "relative/folder", exe: "TESV.exe", prefix: "relative/prefix");
        var other = Home().Combine("Games/other");
        _fileSystem.CreateDirectory(other);
        _fileSystem.WriteAllText(other.Combine("Other.exe"), "");
        WriteGame(root, "other", service: null, serviceId: null, runner: "wine", directory: other.GetFullPath(), exe: "Other.exe", prefix: Home().Combine("p").GetFullPath());
        var otherGame = new FakeGameData("Other", primaryFile: "Other.exe");

        var result = NewLocator(root, SharedSkyrim(), SharedEnderal(), otherGame).Locate(new CollectingProblemSink()).Should().ContainSingle().Subject;

        result.Game.Should().BeSameAs(otherGame);
    }

    [Fact]
    public void Locate_RowWithARelativeDirectory_SkipsThatRowAndLocatesTheRest()
    {
        var root = NativeRoot();
        WriteGame(root, "broken", service: null, serviceId: null, runner: "wine", directory: "relative/folder", exe: "Broken.exe", prefix: "relative/prefix");
        var other = Home().Combine("Games/other");
        _fileSystem.CreateDirectory(other);
        _fileSystem.WriteAllText(other.Combine("Other.exe"), "");
        WriteGame(root, "other", service: null, serviceId: null, runner: "wine", directory: other.GetFullPath(), exe: "Other.exe", prefix: Home().Combine("p").GetFullPath());
        var otherGame = new FakeGameData("Other", primaryFile: "Other.exe");

        var result = NewLocator(root, new FakeGameData("Broken", primaryFile: "Broken.exe"), otherGame).Locate().Should().ContainSingle().Subject;

        result.Game.Should().BeSameAs(otherGame);
    }

    private static FakeGameData SharedSkyrim() => new("Skyrim", primaryFile: "TESV.exe");

    private static FakeIdentifiedGameData SharedEnderal()
        => new("Enderal", "TESV.exe", ["Enderal Launcher.exe", "Data/Enderal - Forgotten Stories.esm"]);

    private AbsolutePath Home() => _fileSystem.GetKnownPath(KnownPath.HomeDirectory);

    // The data folder is a real directory because pga.db is opened by SQLite itself; the config folder is in
    // the in-memory file system like everything else the locator reads through IFileSystem.
    private string DataDir(LutrisRoot root) => root.DataDir.GetFullPath();

    private LutrisRoot NativeRoot() => MakeRoot("native", isFlatpak: false);

    private LutrisRoot FlatpakRoot() => MakeRoot("flatpak", isFlatpak: true);

    private LutrisRoot MakeRoot(string name, bool isFlatpak) =>
        new(_fileSystem.FromUnsanitizedFullPath(Path.Combine(_hostDir, name, "data/lutris")), Home().Combine($"{name}/config/lutris"), isFlatpak);

    private void WriteGame(LutrisRoot root, string slug, string? service, string? serviceId, string runner, string? directory, string exe, string? prefix, string? wineVersion = null, int installed = 1, bool writeYaml = true, string? yaml = null)
    {
        var dataDir = DataDir(root);
        Directory.CreateDirectory(dataDir);
        var configPath = $"{slug}-1700000000";

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "pga.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE IF NOT EXISTS games (id INTEGER PRIMARY KEY, slug TEXT, runner TEXT, directory TEXT, installed INTEGER, configpath TEXT, service TEXT, service_id TEXT)";
            create.ExecuteNonQuery();

            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO games (slug, runner, directory, installed, configpath, service, service_id) VALUES ($slug, $runner, $directory, $installed, $configpath, $service, $serviceId)";
            insert.Parameters.AddWithValue("$slug", slug);
            insert.Parameters.AddWithValue("$runner", runner);
            insert.Parameters.AddWithValue("$directory", (object?)directory ?? DBNull.Value);
            insert.Parameters.AddWithValue("$installed", installed);
            insert.Parameters.AddWithValue("$configpath", configPath);
            insert.Parameters.AddWithValue("$service", (object?)service ?? DBNull.Value);
            insert.Parameters.AddWithValue("$serviceId", (object?)serviceId ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        if (!writeYaml) return;

        yaml ??= $"""
            game:
              exe: '{exe}'
              {(prefix is null ? "" : $"prefix: '{prefix}'")}
              working_dir: '{directory}'
            {(wineVersion is null ? "" : $"wine:\n  version: {wineVersion}")}
            """;
        var file = root.ConfigDir.Combine($"games/{configPath}.yml");
        _fileSystem.CreateDirectory(file.Parent);
        _fileSystem.WriteAllText(file, yaml);
    }

    private static IGameData Game(string name, long? gogId = null, string? epicId = null, string? primaryFile = null)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns(name);
        game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(1))
        {
            GOGProductIds = gogId is null ? [] : [gogId.Value],
            EGSCatalogItemId = epicId is null ? [] : [epicId],
        });
        game.GetPrimaryFile(Arg.Any<GameInstallation>()).Returns(new GamePath(LocationId.Game, primaryFile ?? "Game.exe"));
        return game;
    }

    private LutrisLocator NewLocator(LutrisRoot root, params IGameData[] games) => NewLocator([root], new RecordingLogger<LutrisLocator>(), games);

    private LutrisLocator NewLocator(LutrisRoot root, ILogger<LutrisLocator> logger, params IGameData[] games) => NewLocator([root], logger, games);

    private LutrisLocator NewLocator(ImmutableArray<LutrisRoot> roots, ILogger<LutrisLocator> logger, params IGameData[] games)
        => new(games, new SingleLoggerFactory(logger), _fileSystem, roots);

    private GameLocatorResult Locate(LutrisRoot root, IGameData game) => Locate([root], game);

    private GameLocatorResult Locate(ImmutableArray<LutrisRoot> roots, IGameData game)
        => NewLocator(roots, new RecordingLogger<LutrisLocator>(), game).Locate().Should().ContainSingle().Subject;

    private sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }
}
