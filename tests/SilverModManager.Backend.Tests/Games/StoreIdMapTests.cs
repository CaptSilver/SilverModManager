using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Backend.Games.Locators;
using SilverModManager.Sdk.Games;
using NSubstitute;
using NexusMods.Paths;

namespace SilverModManager.Backend.Tests.Games;

public class StoreIdMapTests
{
    private static IGameData Game(string name, params long[] gogIds)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns(name);
        game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(1)) { GOGProductIds = [..gogIds] });
        return game;
    }

    [Fact]
    public void Build_DuplicateId_KeepsTheFirstGameAndRecordsBoth()
    {
        var first = Game("First", 7);
        var second = Game("Second", 7, 8);
        var logger = new RecordingLogger<StoreIdMapTests>();

        var map = StoreIdMap.Build([first, second], game => game.StoreIdentifiers.GOGProductIds, "GOG", "TestLocator", logger);

        map.Claimants[7].Should().ContainSingle().Which.Should().BeSameAs(first);
        map.Claimants[8].Should().ContainSingle().Which.Should().BeSameAs(second);
        var problem = map.Problems.Should().ContainSingle().Subject;
        problem.Locator.Should().Be("TestLocator");
        problem.Message.Should().Contain("First").And.Contain("Second").And.Contain("GOG").And.Contain("7");
        logger.Count(LogLevel.Error).Should().Be(1);
    }

    [Fact]
    public void Build_SameGameListingAnIdTwice_IsNotAProblem()
    {
        var game = Game("Only", 7, 7);

        var map = StoreIdMap.Build([game], g => g.StoreIdentifiers.GOGProductIds, "GOG", "TestLocator", new RecordingLogger<StoreIdMapTests>());

        map.Claimants.Should().ContainSingle();
        map.Problems.Should().BeEmpty();
    }

    private static readonly RelativePath LondonFile = RelativePath.FromUnsanitizedInput("Data/LondonWorldSpace.esm");

    private static FakeGameData Plain(string name, long id) => new(name) { StoreIdentifiers = new StoreIdentifiers(GameId.From(name)) { GOGProductIds = [id] } };

    private static FakeIdentifiedGameData Identified(string name, long id, params RelativePath[] files)
        => new(name, "game.exe", files) { StoreIdentifiers = new StoreIdentifiers(GameId.From(name)) { GOGProductIds = [id] } };

    private static StoreIdMap<long> Map(params IGameData[] games)
        => StoreIdMap.Build(games, game => game.StoreIdentifiers.GOGProductIds, "GOG", "TestLocator", new RecordingLogger<StoreIdMapTests>());

    private static AbsolutePath Folder(params RelativePath[] files)
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var folder = fileSystem.FromUnsanitizedFullPath("/games/shared");
        fileSystem.CreateDirectory(folder);
        foreach (var file in files)
        {
            fileSystem.CreateDirectory(folder.Combine(file).Parent);
            fileSystem.WriteAllText(folder.Combine(file), "");
        }

        return folder;
    }

    [Fact]
    public void Build_TwoGamesThatNameDifferentFiles_KeepsBothWithoutAProblem()
    {
        var map = Map(Plain("Base", 9), Identified("London", 9, LondonFile));

        map.Claimants[9].Select(game => game.DisplayName).Should().Equal("Base", "London");
        map.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Build_TwoGamesThatNameTheSameFilesInAnotherCase_IsAClash()
    {
        var map = Map(Identified("One", 9, LondonFile), Identified("Two", 9, RelativePath.FromUnsanitizedInput("data/londonworldspace.esm")));

        map.Claimants[9].Should().ContainSingle().Which.DisplayName.Should().Be("One");
        map.Problems.Should().ContainSingle();
    }

    [Fact]
    public void Resolve_FolderWithTheFile_IsTheGameThatNamesIt()
    {
        var map = Map(Plain("Base", 9), Identified("London", 9, LondonFile));

        map.Resolve(9, Folder(LondonFile), problems: null)!.DisplayName.Should().Be("London");
    }

    [Fact]
    public void Resolve_FolderWithoutTheFile_IsTheGameThatNamesNone()
    {
        var map = Map(Identified("London", 9, LondonFile), Plain("Base", 9));

        map.Resolve(9, Folder(), problems: null)!.DisplayName.Should().Be("Base");
    }

    [Fact]
    public void Resolve_AnIdNoGameClaims_IsNull()
    {
        Map(Plain("Base", 9)).Resolve(10, Folder(), problems: null).Should().BeNull();
    }

    [Fact]
    public void Resolve_AFolderThatTwoIdentifiedGamesFitInEqually_KeepsTheFirstAndReports()
    {
        var other = RelativePath.FromUnsanitizedInput("Data/Other.esm");
        var map = Map(Identified("One", 9, LondonFile), Identified("Two", 9, other));
        var sink = new CollectingProblemSink();

        map.Resolve(9, Folder(LondonFile, other), sink)!.DisplayName.Should().Be("One");

        sink.Problems.Should().ContainSingle().Which.Message.Should().Contain("One").And.Contain("Two").And.Contain("GOG").And.Contain("9");
    }

    [Fact]
    public void Default_IsTheGameThatNamesNoFiles()
    {
        var map = Map(Identified("London", 9, LondonFile), Plain("Base", 9));

        map.Default(9)!.DisplayName.Should().Be("Base");
        map.Claims(9, map.Claimants[9][0]).Should().BeTrue();
    }
}
