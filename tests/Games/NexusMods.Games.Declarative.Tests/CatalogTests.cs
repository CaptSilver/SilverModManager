using FluentAssertions;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games.Declarative;
using NexusMods.Games.Declarative.Capabilities;
using NexusMods.Games.Declarative.Catalog;
using NexusMods.Games.Declarative.Schema;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Declarative.Tests;

public class CatalogTests
{
    private readonly InMemoryFileSystem _fileSystem = new(OSInformation.FakeUnix);
    private readonly RecordingLogger<GameDefinitionCatalog> _logger = new();

    private AbsolutePath UserFolder => _fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine($"{ApplicationConstants.DataDirectoryName}/games");

    private static void Write(AbsolutePath file, string text)
    {
        using var writer = new StreamWriter(file.Create());
        writer.Write(text);
    }

    private AbsolutePath WriteUser(string fileName, string yaml)
    {
        var file = UserFolder.Combine(fileName);
        file.Parent.CreateDirectory();
        Write(file, yaml);
        return file;
    }

    private GameDefinitionCatalog Catalog(bool loadUserDefinitions = true, IEnumerable<DefinitionSource>? bundled = null, Func<IEnumerable<IGameData>>? coded = null)
        => new(_fileSystem, new CapabilityRegistry([new SampleCapability()]), _logger, loadUserDefinitions, bundled, coded);

    private static string[] Ids(GameDefinitionCatalog catalog) => catalog.Definitions.Select(loaded => loaded.Definition.Id).ToArray();

    [Fact]
    public void BundledDefinitionsAreLoaded()
    {
        var catalog = Catalog(loadUserDefinitions: false, bundled: [new("bundled/a.yaml", Samples.Minimal("game-a", 1)), new("bundled/b.yaml", Samples.Minimal("game-b", 2))]);

        Ids(catalog).Should().Equal("game-a", "game-b");
        catalog.Definitions.Should().OnlyContain(loaded => loaded.IsBundled);
        _logger.Messages(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public void UserDefinitionsAreLoadedFromTheConfigFolder()
    {
        WriteUser("mine.yaml", Samples.Minimal("my-game", 5));

        var catalog = Catalog();

        Ids(catalog).Should().Equal("my-game");
        catalog.Definitions[0].IsBundled.Should().BeFalse();
        catalog.TryGet(GameId.From("my-game"), out var found).Should().BeTrue();
        found!.Source.Should().Be(UserFolder.Combine("mine.yaml").ToString());
    }

    [Fact]
    public void UserDefinitionsAreNotReadUnlessTheHostAsks()
    {
        WriteUser("mine.yaml", Samples.Minimal("my-game", 5));

        Ids(Catalog(loadUserDefinitions: false)).Should().BeEmpty();
    }

    [Fact]
    public void ANonYamlFileInTheUserFolderIsIgnored()
    {
        WriteUser("notes.txt", "not a definition");

        Catalog().Definitions.Should().BeEmpty();
        _logger.Messages(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public void AUserIdEqualToABundledIdIsRejectedNamingBothFiles()
    {
        var user = WriteUser("clash.yaml", Samples.Minimal("game-a", 99));

        var catalog = Catalog(bundled: [new("bundled/a.yaml", Samples.Minimal("game-a", 1))]);

        catalog.Definitions.Should().ContainSingle().Which.Source.Should().Be("bundled/a.yaml");
        _logger.Messages(LogLevel.Error).Should().ContainSingle().Which.Should().Contain(user.ToString()).And.Contain("bundled/a.yaml");
    }

    [Fact]
    public void AUserFileThatFailsValidationIsSkippedAndTheOthersStillLoad()
    {
        var bad = WriteUser("a-bad.yaml", Samples.Minimal("Bad_Id", 7));
        WriteUser("b-good.yaml", Samples.Minimal("good", 8));

        var catalog = Catalog();

        Ids(catalog).Should().Equal("good");
        _logger.Messages(LogLevel.Error).Should().ContainSingle().Which.Should().Contain(bad.ToString()).And.Contain("id");
    }

    [Fact]
    public void AUserFileThatIsNotValidYamlIsSkippedAndTheOthersStillLoad()
    {
        var broken = WriteUser("a-broken.yaml", "id: [unclosed");
        var unknownKey = WriteUser("b-typo.yaml", Samples.Minimal("typo", 7, extra: "primaryExee: x"));
        WriteUser("c-good.yaml", Samples.Minimal("good", 8));

        Ids(Catalog()).Should().Equal("good");
        _logger.Messages(LogLevel.Error).Should().HaveCount(2)
            .And.Contain(message => message.Contains(broken.ToString()))
            .And.Contain(message => message.Contains(unknownKey.ToString()) && message.Contains("primaryExee"));
    }

    [Fact]
    public void AStoreIdUsedByTwoDefinitionsRejectsTheLaterOne()
    {
        var user = WriteUser("later.yaml", Samples.Minimal("later", 22330));

        var catalog = Catalog(bundled: [new("bundled/first.yaml", Samples.Minimal("first", 22330))]);

        Ids(catalog).Should().Equal("first");
        _logger.Messages(LogLevel.Error).Should().ContainSingle().Which.Should().Contain(user.ToString()).And.Contain("bundled/first.yaml").And.Contain("Steam app id 22330");
    }

    [Theory]
    [InlineData("gog: [5]", "GOG product id 5")]
    [InlineData("epic: [e1]", "Epic catalog item id e1")]
    [InlineData("amazon: [a1]", "Amazon product id a1")]
    public void EveryStoreIsCheckedForRepeatedIds(string store, string expected)
    {
        var yaml = (string id) => $$"""
            id: {{id}}
            name: {{id}}
            stores: { {{store}} }
            primaryExe: Game.exe
            locations: { Game: { root: install } }
            """;

        var catalog = Catalog(loadUserDefinitions: false, bundled: [new("a.yaml", yaml("one")), new("b.yaml", yaml("two"))]);

        Ids(catalog).Should().Equal("one");
        _logger.Messages(LogLevel.Error).Should().ContainSingle().Which.Should().Contain(expected);
    }

    [Fact]
    public void TheSameNexusIdMayBeUsedByTwoDefinitions()
    {
        string Definition(string id, uint steam) => Samples.Minimal(id, steam, extra: "nexus: { domain: skyrimspecialedition, id: 1704 }");

        var catalog = Catalog(loadUserDefinitions: false, bundled: [new("a.yaml", Definition("skyrim-se-like", 1)), new("b.yaml", Definition("enderal-se-like", 2))]);

        Ids(catalog).Should().Equal("skyrim-se-like", "enderal-se-like");
        _logger.Messages(LogLevel.Error).Should().BeEmpty();
        catalog.Definitions.Select(loaded => loaded.Definition.GetGameId()).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void BundledDefinitionsInTheAssemblyAllPassEveryRule()
    {
        var catalog = Catalog(loadUserDefinitions: false);

        _logger.Messages(LogLevel.Error).Should().BeEmpty();
        var resources = typeof(GameDefinitionCatalog).Assembly.GetManifestResourceNames().Count(name => name.StartsWith("games/") && name.EndsWith(".yaml"));
        catalog.Definitions.Should().HaveCount(resources);
    }

    [Fact]
    public void MatchFolderFindsTheSingleDefinitionWhosePrimaryExeIsThere()
    {
        var catalog = Catalog(loadUserDefinitions: false, bundled: [new("a.yaml", Samples.Minimal("game-a", 1, "A.exe")), new("b.yaml", Samples.Minimal("game-b", 2, "B.exe"))]);
        var folder = _fileSystem.FromUnsanitizedFullPath("/games/a");
        folder.CreateDirectory();
        Write(folder.Combine("A.exe"), "");

        catalog.MatchFolder(folder).Should().Equal(GameId.From("game-a"));
    }

    [Fact]
    public void MatchFolderReturnsEveryCandidateWhenSeveralShareAnExe()
    {
        var catalog = Catalog(loadUserDefinitions: false, bundled: [new("a.yaml", Samples.Minimal("skyrim-like", 1, "SkyrimSE.exe")), new("b.yaml", Samples.Minimal("enderal-like", 2, "SkyrimSE.exe"))]);
        var folder = _fileSystem.FromUnsanitizedFullPath("/games/skyrim");
        folder.CreateDirectory();
        Write(folder.Combine("SkyrimSE.exe"), "");

        catalog.MatchFolder(folder).Should().BeEquivalentTo([GameId.From("skyrim-like"), GameId.From("enderal-like")]);
    }

    [Fact]
    public void MatchFolderReturnsNothingWhenNoPrimaryExeIsThere()
    {
        var catalog = Catalog(loadUserDefinitions: false, bundled: [new("a.yaml", Samples.Minimal("game-a", 1, "A.exe"))]);
        var folder = _fileSystem.FromUnsanitizedFullPath("/games/empty");
        folder.CreateDirectory();
        Write(folder.Combine("Other.exe"), "");

        catalog.MatchFolder(folder).Should().BeEmpty();
    }

    [Fact]
    public void MatchFolderReturnsNothingForAFolderThatDoesNotExist()
    {
        var catalog = Catalog(loadUserDefinitions: false, bundled: [new("a.yaml", Samples.Minimal("game-a", 1, "A.exe"))]);

        catalog.MatchFolder(_fileSystem.FromUnsanitizedFullPath("/not/mounted")).Should().BeEmpty();
    }

    [Fact]
    public void MatchFolderIgnoresTheCaseOfTheFileAndItsFolders()
    {
        var catalog = Catalog(loadUserDefinitions: false, bundled: [new("a.yaml", Samples.Minimal("game-a", 1, "bin/x64/Game.exe"))]);
        var folder = _fileSystem.FromUnsanitizedFullPath("/games/a");
        folder.Combine("BIN/X64").CreateDirectory();
        Write(folder.Combine("BIN/X64/game.EXE"), "");

        catalog.MatchFolder(folder).Should().Equal(GameId.From("game-a"));
    }

    [Fact]
    public void MatchFolderIncludesCodedGames()
    {
        var coded = new FakeGame("coded-game", () => new GamePath(LocationId.Game, "bin/Coded.exe"));
        var codedId = coded.GameId;
        var catalog = Catalog(loadUserDefinitions: false, bundled: [new("a.yaml", Samples.Minimal("game-a", 1, "A.exe"))], coded: () => [coded]);
        var folder = _fileSystem.FromUnsanitizedFullPath("/games/coded");
        folder.Combine("bin").CreateDirectory();
        Write(folder.Combine("bin/Coded.exe"), "");

        catalog.MatchFolder(folder).Should().Equal(codedId);
    }

    [Fact]
    public void MatchFolderSkipsACodedGameWhosePrimaryFileIsOutsideTheGameFolder()
    {
        var coded = new FakeGame("saves-launcher", () => new GamePath(LocationId.Saves, "Launch.exe"));
        var catalog = Catalog(loadUserDefinitions: false, coded: () => [coded]);
        var folder = _fileSystem.FromUnsanitizedFullPath("/games/saves");
        folder.CreateDirectory();
        Write(folder.Combine("Launch.exe"), "");

        catalog.MatchFolder(folder).Should().BeEmpty();
    }

    [Fact]
    public void MatchFolderSkipsACodedGameThatNeedsAnInstallationToNameItsExe()
    {
        var coded = new FakeGame("needs-installation", () => throw new InvalidOperationException("no store"));
        var catalog = Catalog(loadUserDefinitions: false, coded: () => [coded]);
        var folder = _fileSystem.FromUnsanitizedFullPath("/games/x");
        folder.CreateDirectory();

        catalog.MatchFolder(folder).Should().BeEmpty();
    }

    [Fact]
    public void WriteMinimalUserDefinitionWritesAFileTheNextLoadReads()
    {
        var catalog = Catalog();

        var id = catalog.WriteMinimalUserDefinition("My Cool Game!", RelativePath.FromUnsanitizedInput("bin/Cool.exe"));

        id.Should().Be(GameId.From("my-cool-game"));
        UserFolder.Combine("my-cool-game.yaml").FileExists.Should().BeTrue();
        var reloaded = Catalog();
        reloaded.Definitions.Should().ContainSingle();
        var definition = reloaded.Definitions[0].Definition;
        definition.Id.Should().Be("my-cool-game");
        definition.Name.Should().Be("My Cool Game!");
        definition.PrimaryExe.Should().Be("bin/Cool.exe");
        definition.Stores.ManualOnly.Should().BeTrue();
        _logger.Messages(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public void WriteMinimalUserDefinitionPicksTheNextFreeSuffixOnCollision()
    {
        var catalog = Catalog(bundled: [new("a.yaml", Samples.Minimal("cool-game", 1))]);
        WriteUser("cool-game-2.yaml", Samples.Minimal("other-name", 2));

        var first = catalog.WriteMinimalUserDefinition("Cool Game", RelativePath.FromUnsanitizedInput("Cool.exe"));
        var second = catalog.WriteMinimalUserDefinition("Cool Game", RelativePath.FromUnsanitizedInput("Cool.exe"));

        first.Should().Be(GameId.From("cool-game-3"));
        second.Should().Be(GameId.From("cool-game-4"));
        Ids(catalog).Should().Contain(["cool-game-3", "cool-game-4"]);
    }

    [Fact]
    public void WriteMinimalUserDefinitionAvoidsTheIdOfACodedGame()
    {
        var coded = new FakeGame("cool-game", () => new GamePath(LocationId.Game, "Cool.exe"));
        var catalog = Catalog(coded: () => [coded]);

        catalog.WriteMinimalUserDefinition("Cool Game", RelativePath.FromUnsanitizedInput("Cool.exe")).Should().Be(GameId.From("cool-game-2"));
    }

    [Fact]
    public void WriteMinimalUserDefinitionRejectsAPrimaryExeOutsideTheGameFolder()
    {
        var catalog = Catalog();

        var act = () => catalog.WriteMinimalUserDefinition("Cool Game", RelativePath.FromUnsanitizedInput("../Cool.exe"));

        act.Should().Throw<ArgumentException>().WithMessage("*primaryExe*");
        UserFolder.DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public void WriteMinimalUserDefinitionRefusesWhenUserDefinitionsAreOff()
    {
        var catalog = Catalog(loadUserDefinitions: false);

        var act = () => catalog.WriteMinimalUserDefinition("Cool Game", RelativePath.FromUnsanitizedInput("Cool.exe"));

        act.Should().Throw<InvalidOperationException>();
    }
}
