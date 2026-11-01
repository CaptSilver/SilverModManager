using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.Generic.Tests.Simple;

/// <summary>Checks that hold for every row of <see cref="SimpleGames.All"/>, so adding a game cannot break a neighbour.</summary>
public class SimpleGamesTableTests(IServiceProvider serviceProvider, IFileSystem fileSystem, TemporaryFileManager fileManager)
{
    private static IEnumerable<SimpleGameDefinition> Rows => SimpleGames.All;

    // One stand-in per root, so a root is checked even if no shipped row uses it.
    private static IEnumerable<SimpleGameDefinition> RowsAndOneOfEachRoot =>
    [
        ..Rows,
        SimpleDefinitions.With(new ModLocation(ModLocationRoot.Documents, "Tests/Mods"), "Tests.Documents"),
        SimpleDefinitions.With(new ModLocation(ModLocationRoot.LocalAppData, "Tests/Mods"), "Tests.LocalAppData"),
        SimpleDefinitions.With(new ModLocation(ModLocationRoot.RoamingAppData, "Tests/Mods"), "Tests.RoamingAppData"),
    ];

    [Fact]
    public void GameIds_AreUnique()
    {
        Rows.Select(row => row.GameId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void GameIds_StartWithSimpleUnlessTheGameWasMigratedFromAClass()
    {
        // A migrated game keeps its old id; every other row is "Simple.<Name>".
        Rows.Select(row => row.GameId.ToString())
            .Where(id => !id.StartsWith("Simple.", StringComparison.Ordinal))
            .Should().BeSubsetOf(["Witcher.Witcher1", "Witcher.Witcher2"]);
    }

    [Fact]
    public void StoreIds_AreUniqueUnlessTheGamesDeclareIdentifyingFiles()
    {
        var claims = Rows
            .SelectMany(row => row.Stores.SteamAppIds.Select(id => (Key: $"steam:{id}", Row: row))
                .Concat(row.Stores.GOGProductIds.Select(id => (Key: $"gog:{id}", Row: row)))
                .Concat(row.Stores.EGSCatalogItemId.Select(id => (Key: $"egs:{id}", Row: row))))
            .GroupBy(claim => claim.Key)
            .Where(group => group.Count() > 1);

        foreach (var shared in claims)
            shared.Should().OnlyContain(claim => claim.Row.IdentifyingFiles.Count > 0, because: $"{shared.Key} is claimed by more than one game");
    }

    [Fact]
    public void EveryRow_HasAnExecutableAndNativeExecutableInsideTheGameFolder()
    {
        foreach (var row in Rows)
        {
            row.Executable.Should().NotBe(RelativePath.Empty, because: row.GameId.ToString());
            row.Executable.ToString().Should().NotStartWith("/").And.NotContain("..");
            if (row.NativeLinuxExecutable is { } native) native.ToString().Should().NotStartWith("/").And.NotContain("..");
        }
    }

    // What the Steam launch entry for Linux names, from the app info of each Steam app. Every game here has a Linux
    // build on Steam; the ones left out have no Linux launch entry, or keep their mods outside the game folder with
    // no place for the native build's mods confirmed.
    public static readonly (string GameId, string Executable)[] NativeExecutables =
    [
        ("Simple.DarkestDungeon", "_linux/darkest.bin.x86_64"),
        ("Simple.KerbalSpaceProgram", "KSP.x86_64"),
        ("Simple.Starbound", "linux/run-client.sh"),
        ("Simple.TeamFortress2", "tf.sh"),
        ("Simple.X4Foundations", "testandlaunch"),
        ("Simple.ShadowrunReturns", "Shadowrun"),
        ("Simple.OxygenNotIncluded", "OxygenNotIncluded"),
        ("Simple.NeverwinterNightsEnhancedEdition", "bin/linux-x86/nwmain-linux"),
    ];

    // Where the native build of a game with a mod root outside the game folder keeps its mods; the Windows folder
    // the row's ModRoot names does not exist for it.
    public static readonly (string GameId, string Folder)[] NativeDataFolders =
    [
        ("Simple.NeverwinterNightsEnhancedEdition", "Neverwinter Nights"),
    ];

    [Fact]
    public void NativeLinuxExecutables_AreExactlyTheOnesSteamNamesForLinux()
    {
        Rows.Where(row => row.NativeLinuxExecutable is not null)
            .Select(row => (row.GameId.ToString(), row.NativeLinuxExecutable!.Value.ToString()))
            .Should().BeEquivalentTo(NativeExecutables.Select(native => (native.GameId, native.Executable)));
    }

    [Fact]
    public void ARootOutsideTheGameFolder_WithANativeExecutable_NamesWhereTheNativeBuildKeepsMods()
    {
        // A native build keeps its Documents and AppData data in the user's Linux folders, not the Windows paths a
        // row names, so without its own folder the mods would be deployed where the game never looks.
        Rows.Where(row => row.ModRoot.Root != ModLocationRoot.GameFolder && row.NativeLinuxExecutable is not null)
            .Select(row => (row.GameId.ToString(), row.NativeLinuxDataFolder?.ToString()))
            .Should().BeEquivalentTo(NativeDataFolders.Select(native => (native.GameId, (string?)native.Folder)));
        Rows.Where(row => row.NativeLinuxDataFolder is not null)
            .Should().OnlyContain(row => row.NativeLinuxExecutable != null && row.ModRoot.Root != ModLocationRoot.GameFolder);
    }

    [Fact]
    public void ARootOutsideTheGameFolder_NamesItsLocationForThePlayer()
    {
        foreach (var row in Rows.Where(row => row.ModRoot.Root != ModLocationRoot.GameFolder))
            row.LocationNames.Should().ContainKey(SimpleLocations.For(row.ModRoot.Root).ToString(), because: row.GameId.ToString());
    }

    [Fact]
    public void EveryLocationAGameDeclares_IsRegisteredWithoutAHashCollision()
    {
        // LocationId is a 16-bit hash of its string; a clash throws when the id is created, so touch every one here
        // and a collision fails this test instead of the app's startup.
        var created = new List<LocationId>();
        foreach (var row in RowsAndOneOfEachRoot)
        {
            var result = Located(row);
            var locations = row.GetLocations(result.Path.FileSystem, result);
            created.AddRange(locations.Keys);
            foreach (var name in row.LocationNames.Keys) created.Add(LocationId.From(name));
        }

        created.Distinct().Select(id => id.ToString()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryLocatedLocation_IsInsideTheFixturesTemporaryRoot()
    {
        // Without an isolated known-folder file system a Documents or AppData root resolves into the real home.
        using var probe = fileManager.CreateFolder("temporary-root-probe");
        var temporaryRoot = probe.Path.Parent;
        foreach (var row in RowsAndOneOfEachRoot)
        {
            var result = Located(row);
            foreach (var (id, path) in row.GetLocations(result.Path.FileSystem, result))
                path.InFolder(temporaryRoot).Should().BeTrue(because: $"{row.GameId} {id} is {path}");
        }
    }

    [Fact]
    public void TheStubbedLocator_PutsEachRowsExecutablesWhereTheGameLooksForThem()
    {
        foreach (var row in Rows)
        {
            var files = new Dictionary<RelativePath, byte[]> { [row.Executable] = [1] };
            if (row.NativeLinuxExecutable is { } native) files[native] = [2];

            using var locator = LocatorFor(row, files);
            var result = locator.Locate().Single();

            // What the game itself looks for, resolved through the locations it declares.
            var installation = new GameInstallation(result, GameLocations.Create(result.Game.GetLocations(result.Path.FileSystem, result)));
            var primary = installation.Locations.ToAbsolutePath(result.Game.GetPrimaryFile(installation));
            primary.FileExists.Should().BeTrue(because: row.GameId.ToString());

            result.Game.NativeLinuxPrimaryFile.Should().Be(row.NativeLinuxExecutable, because: row.GameId.ToString());
            if (result.Game.NativeLinuxPrimaryFile is { } nativePrimary)
            {
                var nativePath = installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, nativePrimary));
                nativePath.FileExists.Should().BeTrue(because: row.GameId.ToString());
                nativePath.Should().NotBe(primary);
            }
        }
    }

    [Fact]
    public void EveryRow_IsRegisteredAsOneGameWithOneLaunchTool()
    {
        var games = serviceProvider.GetServices<IGame>().ToArray();
        var tools = serviceProvider.GetServices<ITool>().OfType<IRunGameTool>().ToArray();

        foreach (var row in Rows)
        {
            games.Where(game => game.GameId == row.GameId).Should().ContainSingle(because: row.GameId.ToString());
            tools.Where(tool => tool.GameIds.Contains(row.GameId)).Should().ContainSingle(because: row.GameId.ToString());
            serviceProvider.GetServices<IGameData>().Where(game => game.GameId == row.GameId).Should().ContainSingle();
        }
    }

    [Fact]
    public void EveryRow_BuildsAGameWhoseIdentityIsTheRows()
    {
        foreach (var row in Rows)
        {
            IGameData game = new SimpleGame(serviceProvider, row);
            game.GameId.Should().Be(row.GameId);
            game.DisplayName.Should().Be(row.DisplayName);
            ((IGame)game).LibraryItemInstallers.Should().NotBeEmpty(because: row.GameId.ToString());
        }
    }

    private UniversalStubbedGameLocator LocatorFor(SimpleGameDefinition row, Dictionary<RelativePath, byte[]>? files = null) => new(
        serviceProvider, fileSystem, fileManager, _ => new SimpleGame(serviceProvider, row), row.GameId.ToString(), files,
        locationsFileSystem: IsolatedKnownFolders.Create(serviceProvider))
    {
        // A Proton build, so a known folder lands in the fixture's prefix rather than in the real home's Linux folders.
        Platform = OSPlatform.Windows,
        WinePrefixPath = fileManager.CreateFolder("wine-prefix").Path,
    };

    private GameLocatorResult Located(SimpleGameDefinition row)
    {
        using var locator = LocatorFor(row);
        return locator.Locate().Single();
    }
}
