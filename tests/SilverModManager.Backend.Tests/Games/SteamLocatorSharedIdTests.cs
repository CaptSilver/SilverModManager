using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Backend.Games.Locators;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Tests.Games;

/// <summary>
/// Two games on one Steam app id (Fallout 4 and Fallout 4 London): the install folder says which one it is, by the
/// files only one of them has.
/// </summary>
public class SteamLocatorSharedIdTests
{
    private const uint AppId = 377160;
    private static readonly RelativePath LondonFile = RelativePath.FromUnsanitizedInput("Data/LondonWorldSpace.esm");
    private static readonly RelativePath OtherFile = RelativePath.FromUnsanitizedInput("Data/Other.esm");

    private static FakeGameData Base(string id = "Base") => new(id, primaryFile: "game.exe") { StoreIdentifiers = Claim(id) };

    private static FakeIdentifiedGameData Identified(string id, params RelativePath[] files)
        => new(id, "game.exe", files) { StoreIdentifiers = Claim(id) };

    private static StoreIdentifiers Claim(string id) => new(GameId.From(id)) { SteamAppIds = [AppId] };

    [Fact]
    public void Locate_FolderWithoutTheIdentifyingFile_IsTheGameThatNamesNone()
    {
        var fixture = new Fixture(withFile: null);

        var results = fixture.Locator(Identified("London", LondonFile), Base()).Locate().ToArray();

        results.Should().ContainSingle().Which.Game.GameId.Should().Be(GameId.From("Base"));
    }

    [Fact]
    public void Locate_FolderWithTheIdentifyingFile_IsTheGameThatNamesIt()
    {
        var fixture = new Fixture(withFile: LondonFile);

        var results = fixture.Locator(Base(), Identified("London", LondonFile)).Locate().ToArray();

        results.Should().ContainSingle().Which.Game.GameId.Should().Be(GameId.From("London"));
    }

    [Fact]
    public void Locate_RegistrationOrderDoesNotDecideWhichGameTheFolderIs()
    {
        var plain = new Fixture(withFile: null).Locator(Identified("London", LondonFile), Base()).Locate().Single();
        var identified = new Fixture(withFile: LondonFile).Locator(Identified("London", LondonFile), Base()).Locate().Single();

        plain.Game.GameId.Should().Be(GameId.From("Base"));
        identified.Game.GameId.Should().Be(GameId.From("London"));
    }

    [Fact]
    public void TryLocate_AskedForTheGameTheFolderIsNot_IsNotFound()
    {
        var fixture = new Fixture(withFile: null);
        var london = Identified("London", LondonFile);
        var plain = Base();
        var locator = fixture.Locator(london, plain);

        locator.TryLocate(london, out _).Should().BeFalse();
        locator.TryLocate(plain, out var result).Should().BeTrue();
        result!.Game.Should().BeSameAs(plain);
    }

    [Fact]
    public void TryLocate_AskedForTheGameTheFolderIs_IsFound()
    {
        var fixture = new Fixture(withFile: LondonFile);
        var london = Identified("London", LondonFile);
        var plain = Base();
        var locator = fixture.Locator(plain, london);

        locator.TryLocate(plain, out _).Should().BeFalse();
        locator.TryLocate(london, out var result).Should().BeTrue();
        result!.Game.Should().BeSameAs(london);
    }

    [Fact]
    public void Locate_TwoGamesNamingNoFiles_KeepsTheFirstAndReportsTheClash()
    {
        var fixture = new Fixture(withFile: null);
        var problems = new CollectingProblemSink();

        var results = fixture.Locator(Base("First"), Base("Second")).Locate(problems).ToArray();

        results.Should().ContainSingle().Which.Game.GameId.Should().Be(GameId.From("First"));
        problems.Problems.Should().ContainSingle().Which.Message.Should().Contain("First").And.Contain("Second");
    }

    [Fact]
    public void Locate_TwoIdentifiedGamesWhoseFilesAreBothThere_KeepsTheFirstAndReportsTheClash()
    {
        var fixture = new Fixture(withFile: LondonFile, alsoWith: OtherFile);
        var problems = new CollectingProblemSink();

        var results = fixture.Locator(Identified("One", LondonFile), Identified("Two", OtherFile)).Locate(problems).ToArray();

        results.Should().ContainSingle().Which.Game.GameId.Should().Be(GameId.From("One"));
        problems.Problems.Should().ContainSingle().Which.Message.Should().Contain("One").And.Contain("Two");
    }

    [Fact]
    public void Locate_TwoIdentifiedGamesAndNoneOfTheirFilesThere_LocatesNothingAndReportsNothing()
    {
        var fixture = new Fixture(withFile: null);
        var problems = new CollectingProblemSink();

        var results = fixture.Locator(Identified("One", LondonFile), Identified("Two", OtherFile)).Locate(problems).ToArray();

        results.Should().BeEmpty();
        problems.Problems.Should().BeEmpty();
    }

    private sealed class Fixture
    {
        private readonly InMemoryFileSystem _fileSystem = new(OSInformation.FakeUnix);

        public Fixture(RelativePath? withFile, RelativePath? alsoWith = null)
        {
            var steam = _fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam");
            Write(steam.Combine("steam.sh"), "");
            var library = $"\"libraryfolders\"\n{{\n\"0\"\n{{\n\"path\" \"{steam}\"\n\"label\" \"\"\n\"contentid\" \"1\"\n\"totalsize\" \"0\"\n\"apps\"\n{{\n}}\n}}\n}}\n";
            Write(steam.Combine("config/libraryfolders.vdf"), library);
            Write(steam.Combine("steamapps/libraryfolders.vdf"), library);
            Write(steam.Combine($"steamapps/appmanifest_{AppId}.acf"), Manifest);

            var folder = steam.Combine($"steamapps/common/Game{AppId}");
            Write(folder.Combine("game.exe"), "");
            foreach (var file in new[] { withFile, alsoWith }.OfType<RelativePath>()) Write(folder.Combine(file), "");
        }

        public IGameLocator Locator(params IGameData[] games)
            => new SteamLocator(games, NullLoggerFactory.Instance, _fileSystem, registry: null);

        private void Write(AbsolutePath path, string contents)
        {
            _fileSystem.CreateDirectory(path.Parent);
            _fileSystem.WriteAllText(path, contents);
        }

        private static string Manifest => $$"""
            "AppState"
            {
                "appid" "{{AppId}}"
                "Universe" "1"
                "name" "Test Game"
                "StateFlags" "4"
                "installdir" "Game{{AppId}}"
                "LastOwner" "76561198000000000"
                "BuildID" "1"
                "InstalledDepots"
                {
                    "{{AppId + 1}}"
                    {
                        "manifest" "123456789"
                        "size" "1"
                    }
                }
            }
            """;
    }
}
