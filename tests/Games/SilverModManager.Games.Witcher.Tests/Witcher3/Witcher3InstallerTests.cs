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
        ("Mod folder at the root", "Example Mod.zip", ["modFoo/content/a.ws"]),
        ("Several mod folders at the root", "Example Mod.zip", ["modOne/content/a.ws", "modTwo/content/b.ws"]),
        ("Mod folder under a version wrapper", "Example Mod.zip", ["MyMod v1.2/modFoo/content/a.ws"]),
        ("Mods folder under a version wrapper", "Example Mod.zip", ["MyMod v1.2/Mods/modFoo/content/a.ws"]),
        ("Mod folder with a script project file", "Example Mod.zip",
            ["modArmorDurability/content/scripts/local/WhereDidMyArmorGo.ws", "modArmorDurability/witcherscript.toml"]),
        ("Mod folder with a readme beside it", "Example Mod.zip", ["modLessScreams/content/scripts/local/less_screams_anno.ws", "readme.url"]),
        ("Mod folder named with an underscore", "Example Mod.zip",
            ["mod_E3Runes/content/blob0.bundle", "mod_E3Runes/content/collision.cache.tmp", "mod_E3Runes/content/metadata.store", "mod_E3Runes/content/texture.cache"]),
        ("Mod folder with bundles and caches", "Example Mod.zip",
            ["modBloodTrails/content/blob0.bundle", "modBloodTrails/content/metadata.store", "modBloodTrails/content/scripts/local/BloodTrails.ws", "modBloodTrails/content/texture.cache"]),
        ("Mod folder with an underscore and a variant suffix", "Example Mod.zip",
            ["mod_WolfArmorsRestored_TypeA/content/blob0.bundle"]),
        ("Wrapper over content", "Example Mod.zip", ["My Cool Mod v1.2/content/scripts/a.ws"]),
        ("Loose game content files", "Example Mod.zip", ["blob0.bundle", "metadata.store", "texture.cache", "readme.txt"]),
        ("Root bin folder", "Example Mod.zip", ["bin/x64/dinput8.dll", "bin/config/base/visuals.ini"]),
        ("Bin folder under a wrapper", "Example Mod.zip", ["Wrap/bin/x64/dinput8.dll"]),
        ("Mods folder with bin and dlc beside it", "Example Mod.zip",
            ["Wrap/Mods/modFoo/content/a.ws", "Wrap/bin/x64/b.dll", "Wrap/dlc/dlcFoo/content/c.bundle"]),
        ("Dlc folder under a wrapper", "Example Mod.zip", ["Wrap/dlc/dlcFoo/content/a.bundle"]),
        ("Dlc content folder under a wrapper", "Example Mod.zip", ["Wrap/dlcFoo/content/a.bundle"]),
        ("Menu mod with a mod folder beside bin", "Example Mod.zip",
            ["bin/config/r4game/user_config_matrix/pc/x.xml", "modFoo/content/a.ws", "dlcFoo/content/b.bundle"]),
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

    private static string[] PlanTargets(string archiveName, params string[] paths) =>
        Witcher3InstallPlan.Plan(paths, archiveName)?.Placements.Select(p => p.Target.ToString()).Order(StringComparer.Ordinal).ToArray() ?? [];

    [Theory]
    [InlineData("Option A/modFoo/content/a.ws", "Option B/modFoo/content/a.ws")]
    [InlineData("Option A/dlc/dlcX/content/a.bundle", "Option B/dlc/dlcX/content/a.bundle")]
    [InlineData("Option A/dlcX/content/a.bundle", "Option B/dlcX/content/a.bundle")]
    [InlineData("dlcA/content/x.bundle", "modB/content/y.ws", "modB/bin/x64/d.dll", "dlcA/bin/x64/d.dll")]
    public void VariantArchives_AreLeftForTheManualInstaller(params string[] paths)
    {
        var plan = Witcher3InstallPlan.Plan(paths, "Example Mod.zip");
        plan.Should().NotBeNull();
        plan!.IsAmbiguous.Should().BeTrue();
        plan.Placements.Should().BeEmpty();
    }

    [Fact]
    public void SeveralContentWrappers_AreVariantsAndTheOutcomeNamesThem()
    {
        var plan = Witcher3InstallPlan.Plan(["Option A/content/blob0.bundle", "Option B/content/blob0.bundle"], "Example Mod.zip");
        plan.Should().NotBeNull();
        plan!.IsAmbiguous.Should().BeTrue();
        plan.Placements.Should().BeEmpty();
        plan.Variants.Should().BeEquivalentTo("Option A", "Option B");
    }

    [Fact]
    public void SeveralContentWrappersWithDifferentFiles_AreStillVariants()
    {
        var plan = Witcher3InstallPlan.Plan(["Option A/content/a.bundle", "Option B/content/b.bundle"], "Example Mod.zip");
        plan.Should().NotBeNull();
        plan!.IsAmbiguous.Should().BeTrue();
    }

    [Fact]
    public async Task SeveralContentWrappers_AreNotSupportedWithAReasonThatNamesTheVariants()
    {
        var loadout = await CreateLoadout();
        var archive = await AddNamedArchive("Example Mod.zip", "Option A/content/a.bundle", "Option B/content/b.bundle");
        var libraryItem = archive.AsLibraryFile().AsLibraryItem();

        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = libraryItem.Name, LoadoutId = loadout },
        };

        var result = await Game.LibraryItemInstallers.OfType<Witcher3ModInstaller>().Single().ExecuteAsync(libraryItem, group, tx, loadout, CancellationToken.None);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("Option A").And.Contain("Option B");
    }

    [Fact]
    public void DlcBelowAModFolderUnderMods_GoesToTheGameDlcFolder()
    {
        PlanTargets("Example Mod.zip", "Mods/modX/dlc/dlcX/content/y.bundle").Should().Equal("{Game}/dlc/dlcX/content/y.bundle");
        PlanTargets("Example Mod.zip", "Mods/modX/dlcX/content/y.bundle").Should().Equal("{Game}/dlc/dlcX/content/y.bundle");
        PlanTargets("Example Mod.zip", "Wrap/Mods/modX/content/a.ws", "Wrap/Mods/modX/dlc/dlcX/content/y.bundle", "Wrap/Mods/modX/dlc/dlcX/readme.txt")
            .Should().Equal("{Game}/Mods/modX/content/a.ws", "{Game}/dlc/dlcX/content/y.bundle");
    }

    [Fact]
    public void RootScriptsBesideAWrapper_GoUnderThatModFolder()
    {
        PlanTargets("Example Mod.zip", "Wrap/content/a.ws", "scripts/game/b.ws")
            .Should().Equal("{Game}/Mods/modWrap/content/a.ws", "{Game}/Mods/modWrap/content/scripts/game/b.ws");
    }

    [Fact]
    public void RootScriptsBesideOneModFolder_GoUnderThatModFolder()
    {
        PlanTargets("Example Mod.zip", "modFoo/content/a.ws", "scripts/game/b.ws")
            .Should().Equal("{Game}/Mods/modFoo/content/a.ws", "{Game}/Mods/modFoo/content/scripts/game/b.ws");
    }

    [Fact]
    public void RootScriptsBesideSeveralModFolders_AreAmbiguous()
    {
        var plan = Witcher3InstallPlan.Plan(["modOne/content/a.ws", "modTwo/content/b.ws", "scripts/game/c.ws"], "Example Mod.zip");
        plan.Should().NotBeNull();
        plan!.IsAmbiguous.Should().BeTrue();
        plan.Variants.Should().BeEquivalentTo("modOne", "modTwo");
    }

    [Fact]
    public async Task VariantArchive_IsNotSupportedWithAReasonThatNamesTheChoice()
    {
        var loadout = await CreateLoadout();
        var archive = await AddNamedArchive("Example Mod.zip", "Option A/dlcX/content/a.bundle", "Option B/dlcX/content/a.bundle");
        var libraryItem = archive.AsLibraryFile().AsLibraryItem();

        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = libraryItem.Name, LoadoutId = loadout },
        };

        var result = await Game.LibraryItemInstallers.OfType<Witcher3ModInstaller>().Single().ExecuteAsync(libraryItem, group, tx, loadout, CancellationToken.None);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("variants").And.Contain("choice");
    }

    [Fact]
    public void NestedDlcFolder_KeepsItsOwnPlaceUnderTheDeepestFolder()
    {
        PlanTargets("Example Mod.zip", "modX/content/a.ws", "modX/dlc/dlcX/content/y.bundle")
            .Should().Equal("{Game}/Mods/modX/content/a.ws", "{Game}/dlc/dlcX/content/y.bundle");
        PlanTargets("Example Mod.zip", "Wrap/content/a.ws", "Wrap/dlc/dlcX/content/y.bundle")
            .Should().Equal("{Game}/Mods/modWrap/content/a.ws", "{Game}/dlc/dlcX/content/y.bundle");
    }

    [Fact]
    public void ScriptsFolder_GoesUnderContentAndLooseScriptsAreNotPlaced()
    {
        PlanTargets("Example Mod.zip", "scripts/game/a.ws").Should().Equal("{Game}/Mods/modExampleMod/content/scripts/game/a.ws");
        PlanTargets("Example Mod.zip", "Wrap/scripts/game/a.ws", "Wrap/readme.txt").Should().Equal("{Game}/Mods/modExampleMod/content/scripts/game/a.ws");
        PlanTargets("Example Mod.zip", "Wrap/content/b.bundle", "Wrap/scripts/game/a.ws")
            .Should().Equal("{Game}/Mods/modWrap/content/b.bundle", "{Game}/Mods/modWrap/content/scripts/game/a.ws");
        PlanTargets("Example Mod.zip", "loose.ws", "blob0.bundle").Should().Equal("{Game}/Mods/modExampleMod/content/blob0.bundle");
        PlanTargets("Example Mod.zip", "loose.ws").Should().BeEmpty();
    }

    [Fact]
    public void RootBinBesideAWrappedContentFolder_GoesToTheGameBin()
    {
        PlanTargets("Example Mod.zip", "Wrap/content/a.ws", "bin/x64/d.dll")
            .Should().Equal("{Game}/Mods/modWrap/content/a.ws", "{Game}/bin/x64/d.dll");
    }

    [Fact]
    public void DocumentationUnderMods_IsNotPlaced()
    {
        PlanTargets("Example Mod.zip", "Mods/modA/content/a.ws", "Mods/modA/readme.txt", "Mods/modA/docs/info.md")
            .Should().Equal("{Game}/Mods/modA/content/a.ws");
    }

    [Fact]
    public void ModFolderPrefix_IsCaseInsensitive()
    {
        PlanTargets("Example Mod.zip", "MODShout/content/x.ws").Should().Equal("{Game}/Mods/MODShout/content/x.ws");
    }

    [Fact]
    public void ModsFolder_AlsoPlacesModAndDlcFoldersOutsideIt()
    {
        PlanTargets("Example Mod.zip", "Mods/modA/content/x.ws", "Other/dlcB/content/y.bundle")
            .Should().Equal("{Game}/Mods/modA/content/x.ws", "{Game}/dlc/dlcB/content/y.bundle");
        PlanTargets("Example Mod.zip", "Mods/modA/content/x.ws", "modB/content/y.ws", "dlcC/content/z.bundle")
            .Should().Equal("{Game}/Mods/modA/content/x.ws", "{Game}/Mods/modB/content/y.ws", "{Game}/dlc/dlcC/content/z.bundle");
    }

    [Fact]
    public void BinBesideContent_GoesToTheGameBinFolder()
    {
        PlanTargets("Example Mod.zip", "Wrap/content/a.ws", "Wrap/bin/x64/d.dll")
            .Should().Equal("{Game}/Mods/modWrap/content/a.ws", "{Game}/bin/x64/d.dll");
        PlanTargets("Example Mod.zip", "modFoo/content/a.ws", "modFoo/bin/x64/d.dll")
            .Should().Equal("{Game}/Mods/modFoo/content/a.ws", "{Game}/bin/x64/d.dll");
    }

    [Fact]
    public void WrapperProjectFile_GoesAlongWithTheContent()
    {
        PlanTargets("Example Mod.zip", "Wrap/content/a.ws", "Wrap/witcherscript.toml", "Wrap/readme.txt")
            .Should().Equal("{Game}/Mods/modWrap/content/a.ws", "{Game}/Mods/modWrap/witcherscript.toml");
    }

    [Fact]
    public void DocumentationInBinAndDlc_IsNotPlaced()
    {
        PlanTargets("Example Mod.zip", "Wrap/Mods/modFoo/content/a.ws", "Wrap/bin/readme.txt", "Wrap/dlc/readme.txt", "Wrap/bin/x64/d.dll")
            .Should().Equal("{Game}/Mods/modFoo/content/a.ws", "{Game}/bin/x64/d.dll");
        PlanTargets("Example Mod.zip", "bin/readme.txt", "bin/x64/d.dll").Should().Equal("{Game}/bin/x64/d.dll");
        PlanTargets("Example Mod.zip", "dlc/readme.txt", "dlc/dlcFoo/content/a.bundle").Should().Equal("{Game}/dlc/dlcFoo/content/a.bundle");
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

    [Fact]
    public void BinInsideAModFolderThatAlsoHoldsANestedDlc_IsPlacedOnce()
    {
        var plan = Witcher3InstallPlan.Plan(
            ["modX/content/a.ws", "modX/dlcY/content/b.bundle", "modX/bin/x64/d.dll"], "Example Mod.zip");
        plan.Should().NotBeNull();
        plan!.IsAmbiguous.Should().BeFalse();
        plan.Placements.Select(p => p.Target.ToString()).Should().BeEquivalentTo(
            ["{Game}/Mods/modX/content/a.ws", "{Game}/dlc/dlcY/content/b.bundle", "{Game}/bin/x64/d.dll"]);
    }

    [Fact]
    public void MenuModWithSeveralContentWrappers_AreVariantsAndTheOutcomeNamesThem()
    {
        var plan = Witcher3InstallPlan.Plan(
            ["modMenu/bin/config/r4game/user_config_matrix/pc/m.xml", "modMenu/Option A/content/a.bundle", "modMenu/Option B/content/a.bundle"], "Example Mod.zip");
        plan.Should().NotBeNull();
        plan!.IsAmbiguous.Should().BeTrue();
        plan.Placements.Should().BeEmpty();
        plan.Variants.Should().BeEquivalentTo("Option A", "Option B");
    }

    [Fact]
    public void VariantsWithTheSameFolderName_AreToldApartByTheirWrapper()
    {
        var plan = Witcher3InstallPlan.Plan(
            ["Option A/modFoo/content/a.bundle", "Option B/modFoo/content/a.bundle", "Option A/scripts/x.ws"], "Example Mod.zip");
        plan.Should().NotBeNull();
        plan!.IsAmbiguous.Should().BeTrue();
        plan.Variants.Should().BeEquivalentTo("Option A/modFoo", "Option B/modFoo");
    }

    [Fact]
    public void VariantsWithDifferentFolderNames_AreNamedByTheFolder()
    {
        var plan = Witcher3InstallPlan.Plan(["Option A/modOne/content/a.ws", "Option B/modTwo/content/a.ws", "Option A/scripts/x.ws"], "Example Mod.zip");
        plan!.Variants.Should().BeEquivalentTo("modOne", "modTwo");
    }

    [Fact]
    public void DocumentationOnlyFolders_AreNeitherWrappersNorVariants()
    {
        PlanTargets("Example Mod.zip", "Wrap/content/a.ws", "Docs/content/readme.txt").Should().Equal("{Game}/Mods/modWrap/content/a.ws");
        var plan = Witcher3InstallPlan.Plan(["Wrap/content/a.ws", "Docs/content/readme.txt"], "Example Mod.zip");
        plan!.IsAmbiguous.Should().BeFalse();
        PlanTargets("Example Mod.zip", "modFoo/content/a.ws", "modDocs/content/readme.txt").Should().Equal("{Game}/Mods/modFoo/content/a.ws");
    }

    [Fact]
    public void RootBinBesideOneModFolder_GoesToTheGameBin()
    {
        PlanTargets("Example Mod.zip", "modFoo/content/a.ws", "bin/x64/d.dll", "bin/readme.txt")
            .Should().Equal("{Game}/Mods/modFoo/content/a.ws", "{Game}/bin/x64/d.dll");
    }

    [Fact]
    public void ScriptsInsideAModFolderBesideContent_GoUnderItsContent()
    {
        PlanTargets("Example Mod.zip", "modFoo/content/a.ws", "modFoo/scripts/game/b.ws")
            .Should().Equal("{Game}/Mods/modFoo/content/a.ws", "{Game}/Mods/modFoo/content/scripts/game/b.ws");
        PlanTargets("Example Mod.zip", "modOne/content/a.ws", "modTwo/content/b.ws", "modOne/scripts/game/c.ws")
            .Should().Equal("{Game}/Mods/modOne/content/a.ws", "{Game}/Mods/modOne/content/scripts/game/c.ws", "{Game}/Mods/modTwo/content/b.ws");
        PlanTargets("Example Mod.zip", "modFoo/content/a.ws", "modFoo/scripts/game/b.ws", "dlcBar/content/c.bundle")
            .Should().Equal("{Game}/Mods/modFoo/content/a.ws", "{Game}/Mods/modFoo/content/scripts/game/b.ws", "{Game}/dlc/dlcBar/content/c.bundle");
        PlanTargets("Example Mod.zip", "Wrap/Mods/modFoo/content/a.ws", "Wrap/Mods/modFoo/scripts/game/b.ws")
            .Should().Equal("{Game}/Mods/modFoo/content/a.ws", "{Game}/Mods/modFoo/content/scripts/game/b.ws");
        PlanTargets("Example Mod.zip", "Wrap/content/a.ws", "Wrap/scripts/game/b.ws")
            .Should().Equal("{Game}/Mods/modWrap/content/a.ws", "{Game}/Mods/modWrap/content/scripts/game/b.ws");
    }

    [Fact]
    public void MenuModWithAWrapperOverContent_PutsItInAModFolderNotTheGameRoot()
    {
        PlanTargets("Example Mod.zip", "Wrap/content/a.ws", "bin/config/r4game/user_config_matrix/pc/x.xml")
            .Should().Equal("{Game}/Mods/modWrap/content/a.ws", "{Game}/bin/config/r4game/user_config_matrix/pc/x.xml");
    }

    [Fact]
    public void MenuModWithAModFolderBesideScripts_SendsScriptsUnderContent()
    {
        PlanTargets("Example Mod.zip", "modFoo/content/a.ws", "modFoo/scripts/game/b.ws", "bin/config/r4game/user_config_matrix/pc/x.xml")
            .Should().Equal("{Game}/Mods/modFoo/content/a.ws", "{Game}/Mods/modFoo/content/scripts/game/b.ws", "{Game}/bin/config/r4game/user_config_matrix/pc/x.xml");
    }

    [Fact]
    public void MixedWithOneModFolder_PutsRootScriptsWithIt()
    {
        PlanTargets("Example Mod.zip", "modOne/content/a.ws", "dlcTwo/content/b.ws", "scripts/game/c.ws")
            .Should().Equal("{Game}/Mods/modOne/content/a.ws", "{Game}/Mods/modOne/content/scripts/game/c.ws", "{Game}/dlc/dlcTwo/content/b.ws");
    }

    [Fact]
    public void MixedWithSeveralModFoldersAndRootScripts_IsAmbiguous()
    {
        var plan = Witcher3InstallPlan.Plan(["modOne/content/a.ws", "modTwo/content/b.ws", "dlcThree/content/c.ws", "scripts/game/d.ws"], "Example Mod.zip");
        plan!.IsAmbiguous.Should().BeTrue();
        plan.Placements.Should().BeEmpty();
        plan.Variants.Should().BeEquivalentTo("modOne", "modTwo");
    }

    /// <summary>Layouts that stress the rules beyond the install cases above.</summary>
    private static readonly string[][] ProbeLayouts =
    [
        ["Option A/modFoo/content/a.bundle", "Option B/modFoo/content/a.bundle", "Option A/scripts/x.ws"],
        ["Wrap/content/a.ws", "Docs/content/readme.txt"],
        ["modFoo/content/a.ws", "scripts/game/b.ws", "bin/x64/d.dll"],
        ["modFoo/content/a.ws", "modFoo/scripts/game/b.ws", "modFoo/witcherscript.toml"],
        ["Wrap/content/a.ws", "bin/config/r4game/user_config_matrix/pc/x.xml"],
        ["modFoo/scripts/game/b.ws", "modFoo/content/a.ws", "bin/config/r4game/user_config_matrix/pc/x.xml"],
        ["modOne/content/a.ws", "dlcTwo/content/b.ws", "scripts/game/c.ws"],
        ["modOne/content/a.ws", "modTwo/content/b.ws", "dlcThree/content/c.ws", "scripts/game/d.ws"],
        ["Mods/modFoo/content/a.ws", "Mods/modFoo/scripts/game/b.ws", "Mods/modFoo/dlc/dlcX/content/y.bundle", "bin/x64/d.dll"],
        ["Wrap/content/a.ws", "Wrap/scripts/game/b.ws", "Wrap/bin/x64/d.dll", "Wrap/dlc/dlcX/content/y.bundle", "scripts/game/c.ws"],
        ["scripts/game/a.ws", "bin/x64/d.dll", "blob0.bundle", "loose.ws"],
        ["Wrap/scripts/game/a.ws", "Wrap/readme.txt"],
        ["Mods/modA/content/x.ws", "Other/dlcB/content/y.bundle"],
        ["dlcA/content/x.bundle", "modB/content/y.ws", "modB/bin/x64/d.dll", "dlcA/bin/x64/d.dll"],
        ["modOne/content/a.ws", "modTwo/content/b.ws"],
        ["Option A/content/a.bundle", "Option B/content/b.bundle"],
    ];

    [Fact]
    public void EveryPlacement_LandsInAFolderTheGameLoads()
    {
        var layouts = TestCases.Select(row => (row.TestName, row.ArchiveName, row.Paths))
            .Concat(ProbeLayouts.Select((paths, i) => ($"probe {i}", "Example Mod.zip", paths)));
        foreach (var (name, archiveName, paths) in layouts)
        {
            var plan = Witcher3InstallPlan.Plan(paths, archiveName);
            foreach (var placement in plan?.Placements ?? [])
                IsLoadedByTheGame(placement.Target.ToString()).Should().BeTrue($"{name}: {placement.Source} went to {placement.Target}");
        }
    }

    private static bool IsLoadedByTheGame(string target)
    {
        const string gamePrefix = "{Game}/";
        var segments = target[gamePrefix.Length..].Split('/');
        var first = segments[0];
        if (first.Equals("Mods", StringComparison.OrdinalIgnoreCase))
        {
            var isModFolder = segments.Length > 2 && segments[1].StartsWith("mod", StringComparison.OrdinalIgnoreCase);
            // Mods/<mod>/content/... or a single project file beside content.
            return isModFolder && (segments.Length == 3 || (segments.Length > 3 && segments[2].Equals("content", StringComparison.OrdinalIgnoreCase)));
        }
        if (first.Equals("dlc", StringComparison.OrdinalIgnoreCase)) return segments.Length > 2;
        if (first.Equals("bin", StringComparison.OrdinalIgnoreCase)) return segments.Length > 1;
        return first.Equals("INI Tweaks", StringComparison.OrdinalIgnoreCase) && segments.Length == 2;
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
