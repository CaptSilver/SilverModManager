using System.Runtime.CompilerServices;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Games.Witcher.Witcher3.Installers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

public class Witcher3InstallerTests(ITestOutputHelper outputHelper) : AWitcher3ArchiveTest<Witcher3InstallerTests>(outputHelper)
{
    public static readonly List<(string TestName, string ArchiveName, string[] Paths)> TestCases =
    [
        ("Menu mod with content", "Example Mod.zip",
            ["modSDL/bin/config/r4game/user_config_matrix/pc/sdl.xml", "modSDL/content/scripts/game/sdl.ws", "modSDL/input.settings.part.txt"]),
        ("Menu mod with a dlc folder", "Example Mod.zip",
            ["modSDL/bin/config/r4game/user_config_matrix/pc/sdl.xml", "modSDL/dlc/dlcSDL/content/x.bundle"]),
        ("Menu mod with a backup xml", "Example Mod.zip",
            ["modX/bin/config/r4game/user_config_matrix/pc/x.xml", "modX/backup/bin/config/r4game/user_config_matrix/pc/x.xml"]),
        ("Menu mod without a folder above bin", "Example Mod.zip",
            ["bin/config/r4game/user_config_matrix/pc/x.xml", "content/scripts/y.ws"]),
        ("Menu mod with an input.xml part", "Example Mod.zip",
            ["modX/bin/config/r4game/user_config_matrix/pc/x.xml", "modX/input.xml.part.txt"]),
        ("Mixed dlc and mod", "Example Mod.zip", ["dlcFoo/content/a.bundle", "modFoo/content/b.ws"]),
        ("Top-level mods segment", "Example Mod.zip", ["Release/Mods/modBar/content/c.ws"]),
        ("Bare content", "Example Mod.zip", ["content/scripts/d.ws"]),
        ("Dlc only", "Example Mod.zip", ["dlc/dlcBaz/content/e.bundle"]),
        ("Dlc without root", "Example Mod.zip", ["dlcBaz/content/e.bundle"]),
    ];

    public static IEnumerable<object[]> TestCaseData() => TestCases.Select(row => (object[])[row.TestName, row.ArchiveName, row.Paths]);

    [Theory]
    [MemberData(nameof(TestCaseData))]
    public async Task CanInstallWitcher3Mods(string testCaseName, string archiveName, string[] archivePaths)
    {
        var loadout = await CreateLoadout();
        var archive = await AddNamedArchive(archiveName, archivePaths);

        var group = await InstallThroughGame(loadout, archive);

        await VerifyChildren(ChildrenFilesAndHashes(group)).UseParameters(testCaseName);
    }

    [Fact]
    public async Task MenuModWithoutBin_RootsAtThePartFolderSoContentLandsUnderMods()
    {
        var loadout = await CreateLoadout();
        var archive = await AddNamedArchive("Example Mod.zip", "modFoo/content/scripts/x.ws", "modFoo/input.settings.part.txt");

        var group = await InstallThroughGame(loadout, archive);

        Targets(group).Should().Equal(
            "{Game}/INI Tweaks/modFoo [input.settings].ini",
            "{Game}/Mods/modFoo/content/scripts/x.ws");
    }

    [Fact]
    public async Task ScriptMerger_IsRefusedReadablyOnAManualInstall()
    {
        var loadout = await CreateLoadout();
        var archive = await AddNamedArchive("Script Merger.zip", "WitcherScriptMerger/WitcherScriptMerger.exe", "WitcherScriptMerger/readme.txt");
        var groupsBefore = LoadoutItemGroup.All(Connection.Db).Count(g => g.AsLoadoutItem().LoadoutId == loadout.LoadoutId);

        var act = async () => await InstallThroughGame(loadout, archive);

        var thrown = (await act.Should().ThrowAsync<ScriptMergerArchiveRefusedException>()).Which;
        thrown.Title.Should().Be("Script Merger is not needed");
        thrown.Message.Should().Contain("Script Merger.zip").And.Contain("merges scripts itself").And.NotContain("collection");
        LoadoutItemGroup.All(Connection.Db).Count(g => g.AsLoadoutItem().LoadoutId == loadout.LoadoutId).Should().Be(groupsBefore);
    }

    [Fact]
    public async Task ScriptMerger_IsSkippedInsideACollection()
    {
        var loadout = await CreateLoadout();
        var collection = await CreateNexusCollectionGroup(loadout, "Big list");
        var archive = await AddNamedArchive("Script Merger.zip", "WitcherScriptMerger/WitcherScriptMerger.exe", "WitcherScriptMerger/readme.txt");

        var group = await InstallThroughGame(loadout, archive, collection);

        group.IsValid().Should().BeTrue();
        group.AsLoadoutItem().ParentId.Value.Should().Be(collection.Value);
        group.TryGetAsLibraryLinkedLoadoutItem(out _).Should().BeTrue("the collection counts a library-linked group as installed");
        group.Children.Should().BeEmpty();
    }

    [Fact]
    public async Task PartTxt_IsNeverPlacedOnDisk()
    {
        var loadout = await CreateLoadout();
        var archive = await AddNamedArchive("Example Mod.zip",
            "modSDL/bin/config/r4game/user_config_matrix/pc/sdl.xml", "modSDL/input.settings.part.txt");

        var group = await InstallThroughGame(loadout, archive);

        Targets(group).Should().NotContain(t => t.EndsWith(".part.txt"));
        var tweak = new GamePath(LocationId.Game, "INI Tweaks/modSDL [input.settings].ini");
        Targets(group).Should().Contain(tweak.ToString());

        var tree = await Synchronizer.BuildSyncTree(Loadout.Load(Connection.Db, loadout.LoadoutId));
        Synchronizer.ProcessSyncTree(tree);
        tree[tweak].Actions.Should().Be(Actions.DoNothing, "the game ignores the tweak folder, so nothing is written there");
    }

    [Fact]
    public async Task UnknownLayout_IsNotSupportedByEveryInstaller()
    {
        var loadout = await CreateLoadout();
        var archive = await AddNamedArchive("Example Mod.zip", "readme.txt");
        var libraryItem = archive.AsLibraryFile().AsLibraryItem();

        Game.LibraryItemInstallers.Should().NotBeEmpty();
        foreach (var installer in Game.LibraryItemInstallers)
        {
            using var tx = Connection.BeginTransaction();
            var group = new LoadoutItemGroup.New(tx, out var groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = libraryItem.Name, LoadoutId = loadout },
            };

            var result = await installer.ExecuteAsync(libraryItem, group, tx, loadout, CancellationToken.None);

            result.IsNotSupported(out _).Should().BeTrue(because: installer.GetType().Name);
        }
    }

    [Theory]
    [InlineData("Example Mod.zip", "modExampleMod")]
    [InlineData("My.Cool Mod v1.2.7z", "modMyCoolModv12")]
    [InlineData("noextension", "modnoextension")]
    public void ModNameFromArchive_DropsTheExtensionSpacesAndDots(string fileName, string expected)
    {
        Witcher3ModInstaller.ModNameFromArchive(fileName).Should().Be(expected);
    }

    private static SettingsTask VerifyChildren(
        IEnumerable<(RelativePath FromPath, Hash Hash, GamePath GamePath)> childrenFilesAndHashes,
        [CallerFilePath] string sourceFile = "")
    {
        return Verify(childrenFilesAndHashes.Select(row => new
            {
                FromPath = row.FromPath.ToString(),
                Hash = row.Hash.ToString(),
                ToGamePath = row.GamePath.ToString(),
            }),
            // ReSharper disable once ExplicitCallerInfoArgument
            sourceFile: sourceFile);
    }
}
