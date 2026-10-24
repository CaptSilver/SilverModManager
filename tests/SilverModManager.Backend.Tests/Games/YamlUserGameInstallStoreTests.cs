using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Backend.Games.UserGames;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Tests.Games;

public class YamlUserGameInstallStoreTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly RecordingLogger<YamlUserGameInstallStore> _logger = new();
    private readonly AbsolutePath _installsFile;

    public YamlUserGameInstallStoreTests()
    {
        _installsFile = _fs.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("SilverModManager/installs.yaml");
    }

    private static IGameData Game(string id, bool nativeLinux = false) => new FakeGameData(id, nativeLinux);

    private YamlUserGameInstallStore Store(params IGameData[] games) => new(_fs, _logger, games);

    private void WriteFile(string yaml)
    {
        _installsFile.Parent.CreateDirectory();
        _installsFile.WriteAllTextAsync(yaml).GetAwaiter().GetResult();
    }

    [Fact]
    public void MissingFile_IsAnEmptyList()
    {
        Store(Game("alpha")).GetAll().Should().BeEmpty();
    }

    [Fact]
    public void ReadsEveryField()
    {
        WriteFile("""
            - game: alpha
              path: /games/alpha
              platform: linux
              prefix: /prefixes/alpha
              wine: /opt/wine/bin/wine
            """);

        Store(Game("alpha")).GetAll().Should().Equal(
            new UserGameInstall(GameId.From("alpha"), "/games/alpha", OSPlatform.Linux, "/prefixes/alpha", "/opt/wine/bin/wine"));
    }

    [Fact]
    public void OmittedPlatform_IsWindows()
    {
        WriteFile("- {game: alpha, path: /games/alpha}\n");

        Store(Game("alpha")).GetAll().Should().ContainSingle().Which.Platform.Should().Be(OSPlatform.Windows);
    }

    [Fact]
    public void OmittedPlatform_IsLinuxWhenTheGameHasANativeBuild()
    {
        WriteFile("- {game: alpha, path: /games/alpha}\n");

        Store(Game("alpha", nativeLinux: true)).GetAll().Should().ContainSingle().Which.Platform.Should().Be(OSPlatform.Linux);
    }

    [Fact]
    public void OmittedPlatform_IsWindowsWhenTheFolderLacksTheNativeExecutable()
    {
        WriteFile("- {game: alpha, path: /games/alpha, prefix: /prefixes/alpha}\n");
        GameFile("/games/alpha/Launcher/Alpha.exe");

        Store(new FakeGameData("alpha", hasNativeLinuxBuild: true, nativePrimaryFile: "bin/alpha"))
            .GetAll().Should().ContainSingle().Which.Platform.Should().Be(OSPlatform.Windows);
    }

    [Fact]
    public void OmittedPlatform_IsLinuxWhenTheFolderHoldsTheNativeExecutable()
    {
        WriteFile("- {game: alpha, path: /games/alpha}\n");
        GameFile("/games/alpha/bin/alpha");

        Store(new FakeGameData("alpha", hasNativeLinuxBuild: true, nativePrimaryFile: "bin/alpha"))
            .GetAll().Should().ContainSingle().Which.Platform.Should().Be(OSPlatform.Linux);
    }

    private void GameFile(string path)
    {
        var file = _fs.FromUnsanitizedFullPath(path);
        file.Parent.CreateDirectory();
        _fs.WriteAllText(file, "");
    }

    [Fact]
    public void MalformedEntries_AreSkippedWithAnErrorAndTheRestLoad()
    {
        WriteFile("""
            - game: alpha
              path: /games/one
            - path: /games/no-game-field
            - game: alpha
              path: /games/bad-platform
              platform: amiga
            - just a string
            - game: alpha
              path: /games/two
            """);

        Store(Game("alpha")).GetAll().Select(install => install.Path).Should().Equal("/games/one", "/games/two");
        _logger.Count(LogLevel.Error).Should().Be(3);
    }

    [Fact]
    public void AFileThatIsNotAList_IsEmptyWithAnError()
    {
        WriteFile("game: alpha\n");

        Store(Game("alpha")).GetAll().Should().BeEmpty();
        _logger.Count(LogLevel.Error).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Add_CreatesTheFileAndAppendsAndTheNextReadSeesIt()
    {
        var store = Store(Game("alpha"));
        var first = new UserGameInstall(GameId.From("alpha"), "/games/one", OSPlatform.Windows, "/prefixes/one");
        var second = new UserGameInstall(GameId.From("alpha"), "/games/two", OSPlatform.Linux);

        store.Add(first);
        store.Add(second);

        store.GetAll().Should().Equal(first, second);
        Store(Game("alpha")).GetAll().Should().Equal(first, second);
    }

    [Theory]
    [InlineData("game: alpha\npath: /games/one\n")]
    [InlineData("- game: alpha\n  path: [unclosed\n")]
    public void Add_RefusesToOverwriteAFileItCouldNotRead(string contents)
    {
        WriteFile(contents);
        var store = Store(Game("alpha"));

        var add = () => store.Add(new UserGameInstall(GameId.From("alpha"), "/games/two", OSPlatform.Linux));

        add.Should().Throw<InvalidOperationException>().WithMessage($"*{_installsFile}*");
        _installsFile.ReadAllTextAsync().GetAwaiter().GetResult().Should().Be(contents);
    }

    [Fact]
    public void Add_KeepsEntriesItCouldNotParse()
    {
        WriteFile("""
            - path: /games/no-game-field
            - game: alpha
              path: /games/one
            """);
        var store = Store(Game("alpha"));

        store.Add(new UserGameInstall(GameId.From("alpha"), "/games/two", OSPlatform.Linux));

        _installsFile.ReadAllTextAsync().GetAwaiter().GetResult().Should().Contain("/games/no-game-field");
        store.GetAll().Select(install => install.Path).Should().Equal("/games/one", "/games/two");
    }
}
