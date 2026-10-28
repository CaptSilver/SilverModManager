using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Games.Witcher.Witcher3.Merge;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.Merge;

/// <summary>What the Witcher 3 tells the merge engine about its paths; nothing here reads a database or a disk.</summary>
public class Witcher3MergeGameTests
{
    private readonly Witcher3MergeGame _game = new(Substitute.For<IGame>());

    private static GamePath Game(string path) => new(LocationId.Game, path);

    [Fact]
    public void Split_IsTheFolderAndTheRestForAModsPath()
    {
        _game.Split(Game("Mods/modA/content/scripts/game/x.ws")).Should().Be(("modA", "content/scripts/game/x.ws"));
        _game.Split(Game("dlc/dlcA/content/x.ws")).Should().BeNull();
        _game.Split(new GamePath(WitcherConstants.WitcherDocuments, "mods.settings")).Should().BeNull();
    }

    [Fact]
    public void Split_KeepsTheFoldersOwnSpelling()
    {
        _game.Split(Game("MODS/ModSDL/content/scripts/x.ws")).Should().Be(("ModSDL", "content/scripts/x.ws"));
    }

    [Fact]
    public void Split_IsNullForTheMergedFolder()
    {
        _game.Split(Game("Mods/mod0000_MergedFiles/content/scripts/x.ws")).Should().BeNull();
        _game.Split(Game("MODS/MOD0000_MERGEDFILES/content/scripts/x.ws")).Should().BeNull();
    }

    [Fact]
    public void Split_IsNullForAFileDirectlyInTheModsFolder()
    {
        _game.Split(Game("Mods/readme.txt")).Should().BeNull();
    }

    [Theory]
    [InlineData("Mods/modA/content/scripts/game/x.ws", true)]
    [InlineData("Mods/modA/content/scripts/game/X.WS", true)]
    [InlineData("Mods/modA/content/x.xml", false)]
    [InlineData("Mods/modA/scripts/x.ws", false)]
    [InlineData("dlc/dlcA/content/scripts/x.ws", false)]
    [InlineData("Mods/modA/content/scripts/game/x.txt", false)]
    public void Merges_OnlyWsUnderContentScripts(string path, bool merges)
    {
        _game.Merges(Game(path)).Should().Be(merges);
    }

    [Fact]
    public void VanillaFor_IsTheContent0Path()
    {
        _game.VanillaFor("content/scripts/game/player.ws", null!)
            .Should().Be(Game("content/content0/scripts/game/player.ws"));
    }

    [Fact]
    public void OutputFor_IsUnderTheMergedFolder()
    {
        _game.OutputFor("content/scripts/game/player.ws")
            .Should().Be(Game("Mods/mod0000_MergedFiles/content/scripts/game/player.ws"));
    }

    [Fact]
    public void StrategyFor_IsTheTextThreeWayMerge()
    {
        _game.StrategyFor("content/scripts/game/player.ws").Should().BeSameAs(TextThreeWayMerge.Instance);
    }

    [Fact]
    public void ReorderHint_IsTheBarePageName()
    {
        _game.ReorderHint.Should().Be("Load Order");
    }

    [Fact]
    public void Contests_EveryPath()
    {
        _game.Contests(Game("Mods/modA/content/scripts/game/x.ws")).Should().BeTrue();
    }
}
