using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>GameInis.AppSetsKey names what the app writes to a key on every Apply, for a managed key and for a derived one.</summary>
public class GameInisAppSetsKeyTests
{
    private static readonly GamePath Main = new(LocationId.Game, "Game.ini");
    private static readonly GamePath Other = new(LocationId.Game, "Other.ini");

    private sealed class StubGame(GamePath? managedFile) : IGameIniFiles
    {
        public IniSet IniFiles { get; } = new("Game.ini", "Other.ini");

        public GamePath IniPath(string fileName) => fileName == "Game.ini" ? Main : Other;

        public GamePath? ManagedKeysFile => managedFile;

        public IReadOnlyList<IniKeyEdit> ManagedKeys(Loadout.ReadOnly loadout) => [new IniKeyEdit("Archive", "bInvalidate", "1")];

        public bool IsDerivedKey(GamePath path, string section, string key) =>
            path == Main && section.Equals("Game Files", StringComparison.OrdinalIgnoreCase) && key == "GameFile0";
    }

    [Fact]
    public void AManagedKeyIsNamedWithItsValueAndMatchedWithoutRegardToCase()
    {
        GameInis.AppSetsKey(new StubGame(Main), default, Main, "archive", "BINVALIDATE").Should().Be("1 on every Apply");
    }

    [Fact]
    public void AManagedKeyOfAnotherFileIsNotManaged()
    {
        GameInis.AppSetsKey(new StubGame(Main), default, Other, "Archive", "bInvalidate").Should().BeNull();
    }

    [Fact]
    public void ADerivedKeyIsRenderedFromTheLoadout()
    {
        GameInis.AppSetsKey(new StubGame(null), default, Main, "Game Files", "GameFile0").Should().Be("rendered from the loadout");
    }

    [Fact]
    public void AnOrdinaryKeyIsNotSetByTheApp()
    {
        GameInis.AppSetsKey(new StubGame(Main), default, Main, "General", "sLanguage").Should().BeNull();
    }
}
