using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.Generic.Tests.Simple;

public class SimpleGameDefinitionTests
{
    [Fact]
    public void GameFolderRoot_MapsOnlyTheGameFolder()
    {
        var prefix = ProtonTestPrefix.Create();
        var definition = SimpleDefinitions.With(new ModLocation(ModLocationRoot.GameFolder, "Mods"));

        var locations = definition.GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations.Keys.Should().Equal(LocationId.Game);
        locations[LocationId.Game].Should().Be(prefix.LocatorResult().Path);
    }

    [Theory]
    [InlineData(ModLocationRoot.Documents, "Documents")]
    [InlineData(ModLocationRoot.LocalAppData, "AppData/Local")]
    [InlineData(ModLocationRoot.RoamingAppData, "AppData/Roaming")]
    public void OtherRoots_ResolveInsideThePrefixTheGameRunsIn(ModLocationRoot root, string knownFolder)
    {
        var prefix = ProtonTestPrefix.Create();
        var definition = SimpleDefinitions.With(new ModLocation(root, "My Game/Mods"));

        var locations = definition.GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations[SimpleLocations.For(root)].Should().Be(prefix.Home.Combine(knownFolder).Combine("My Game/Mods"));
        locations.Should().ContainKey(LocationId.Game, "the executable and the diagnostics still live in the game folder");
    }

    [Theory]
    [InlineData(ModLocationRoot.Documents)]
    [InlineData(ModLocationRoot.LocalAppData)]
    [InlineData(ModLocationRoot.RoamingAppData)]
    public void NoRoot_MapsTheSavesLocation(ModLocationRoot root)
    {
        var prefix = ProtonTestPrefix.Create();
        var definition = SimpleDefinitions.With(new ModLocation(root, "My Game"));

        // Removing a loadout deletes what is mapped, so saves must never be reachable through a mod root.
        definition.GetLocations(prefix.GameFileSystem, prefix.LocatorResult()).Should().NotContainKey(LocationId.Saves);
    }

    [Fact]
    public void ModLocation_InTheGameFolder_IsAGamePathBelowTheGameLocation()
    {
        new ModLocation(ModLocationRoot.GameFolder, "Data/Override").ToGamePath(RelativePath.Empty)
            .Should().Be(new GamePath(LocationId.Game, "Data/Override"));
        new ModLocation(ModLocationRoot.GameFolder, "Data/Override").ToGamePath("sub/dir")
            .Should().Be(new GamePath(LocationId.Game, "Data/Override/sub/dir"));
        new ModLocation(ModLocationRoot.GameFolder, RelativePath.Empty).ToGamePath("CookedPC")
            .Should().Be(new GamePath(LocationId.Game, "CookedPC"));
    }

    [Theory]
    [InlineData(ModLocationRoot.Documents)]
    [InlineData(ModLocationRoot.LocalAppData)]
    [InlineData(ModLocationRoot.RoamingAppData)]
    public void ModLocation_OutsideTheGameFolder_IsRelativeToItsOwnSharedLocation(ModLocationRoot root)
    {
        // The folder is part of the location the definition maps, so a path inside it starts at the location.
        new ModLocation(root, "My Game/Mods").ToGamePath("sub")
            .Should().Be(new GamePath(SimpleLocations.For(root), "sub"));
    }

    [Fact]
    public void SimpleLocations_AreThreeDistinctIdsThatClashWithNoBuiltInLocation()
    {
        var ids = new[] { SimpleLocations.Documents, SimpleLocations.LocalAppData, SimpleLocations.RoamingAppData };

        ids.Should().OnlyHaveUniqueItems();
        ids.Select(id => id.ToString()).Should().Equal("Simple.Documents", "Simple.LocalAppData", "Simple.RoamingAppData");
        ids.Should().NotContain([LocationId.Game, LocationId.Saves, LocationId.Preferences, LocationId.AppData, LocationId.AppDataRoaming]);
    }

    [Fact]
    public void SimpleLocations_ForTheGameFolder_IsTheGameLocation()
    {
        SimpleLocations.For(ModLocationRoot.GameFolder).Should().Be(LocationId.Game);
    }

    [Fact]
    public void LocationNames_AreTheOnesTheDefinitionDeclares()
    {
        var definition = SimpleDefinitions.With(new ModLocation(ModLocationRoot.Documents, "My Game")) with
        {
            LocationNames = new Dictionary<string, string> { [SimpleLocations.Documents.ToString()] = "Mods folder" },
            LocationSentenceNames = new Dictionary<string, string> { [SimpleLocations.Documents.ToString()] = "the mods folder" },
        };

        ((IGameDefinition)definition).LocationNames.Should().ContainKey("Simple.Documents").WhoseValue.Should().Be("Mods folder");
        ((IGameDefinition)definition).LocationSentenceNames.Should().ContainKey("Simple.Documents").WhoseValue.Should().Be("the mods folder");
    }
}
