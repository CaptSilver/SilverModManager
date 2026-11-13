using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class LoadOrderFileSynchronizerTests
{
    private static readonly LoadOrderFileGameDefinition Definition = LoadOrderFileGames.KingdomComeDeliverance with
    {
        OrderFile = new OrderFileLocation(ModLocationRoot.LocalAppDataLow, "Studio/Game/Config/ModsConfig.xml"),
        GameWrittenPaths = [new GamePath(LocationId.Game, "saves"), new GamePath(LocationId.Game, "starsector-core/starsector.log")],
    };

    [Theory]
    [InlineData("Simple.LocalAppDataLow", "Prefs.xml", true)]
    [InlineData("Simple.LocalAppDataLow", "ModsConfig.xml", false)]
    [InlineData("Simple.LocalAppDataLow", "", false)]
    [InlineData("Simple.LocalAppDataLow", "logs/output.txt", true)]
    [InlineData("Game", "saves/foo.save", true)]
    [InlineData("Game", "saves", true)]
    [InlineData("Game", "starsector-core/starsector.log", true)]
    [InlineData("Game", "mods/x/mod_info.json", false)]
    public void TheGameWritesWhatSitsBesideItsOrderFileAndWhatTheRowListsAndNothingElse(string location, string path, bool expected)
    {
        var id = location == "Game" ? LocationId.Game : LocationId.From(location);

        LoadOrderFileSynchronizer.IsGameWritten(Definition, new GamePath(id, path)).Should().Be(expected);
    }

    [Fact]
    public void AnOrderFileInTheGameFolderFiltersNothingInIt()
    {
        LoadOrderFileSynchronizer.IsGameWritten(LoadOrderFileGames.KingdomComeDeliverance, new GamePath(LocationId.Game, "Mods/x/a.pak")).Should().BeFalse();
    }
}
