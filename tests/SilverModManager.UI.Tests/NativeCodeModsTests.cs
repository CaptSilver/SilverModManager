using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.Fallout4;
using SilverModManager.Games.CreationEngine.OblivionRemastered;
using SilverModManager.Games.CreationEngine.SkyrimSE;
using SilverModManager.Games.CreationEngine.Starfield;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.Unity.Peak;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests;

public class NativeCodeModsTests(IServiceProvider provider) : AUiTest(provider)
{
    private IGameNativeCode Game<TGame>() where TGame : IGameNativeCode =>
        Provider.GetServices<IGame>().OfType<TGame>().Single();

    private static GamePath InGame(string path) => new(LocationId.Game, path);

    [Theory]
    [InlineData("skse64_loader.exe", true)]
    [InlineData("skse64_1_6_1170.dll", true)]
    [InlineData("SKSE64_1_6_1170.DLL", true)]
    [InlineData("Data/SKSE/Plugins/EngineFixes.dll", true)]
    [InlineData("Data/SKSE/Plugins/deep/Nested.dll", true)]
    [InlineData("data/skse/plugins/Lower.dll", true)]
    [InlineData("Data/SKSE/Plugins/EngineFixes.ini", false)]
    [InlineData("Data/SKSE/Plugins/EngineFixes.toml", false)]
    [InlineData("Data/Skyrim.esm", false)]
    [InlineData("Data/textures/skse64_decal.dll", false)]
    [InlineData("Data/Scripts/Source/SKSE.psc", false)]
    [InlineData("myskse64_x.dll", false)]
    [InlineData("Data/Plugins/Other.dll", false)]
    public void SkyrimSeNamesTheExtenderAndItsPluginDlls(string path, bool hooks)
        => Game<SkyrimSE>().HooksGameCode(InGame(path)).Should().Be(hooks);

    [Theory]
    [InlineData("f4se_loader.exe", true)]
    [InlineData("f4se_1_10_984.dll", true)]
    [InlineData("winhttp.dll", true)]
    [InlineData("IpHlpAPI.dll", true)]
    [InlineData("xSE PluginPreloader.xml", false)]
    [InlineData("Data/F4SE/Plugins/BetterConsole.dll", true)]
    [InlineData("Data/SKSE/Plugins/BetterConsole.dll", false)]
    [InlineData("Data/F4SE/Plugins/BetterConsole.ini", false)]
    [InlineData("Data/Fallout4.esm", false)]
    public void Fallout4NamesTheExtenderPreloaderAndPluginDlls(string path, bool hooks)
        => Game<Fallout4>().HooksGameCode(InGame(path)).Should().Be(hooks);

    [Theory]
    [InlineData("sfse_loader.exe", true)]
    [InlineData("sfse_1_16_244.dll", true)]
    [InlineData("Data/SFSE/Plugins/Baka.dll", true)]
    [InlineData("Data/SFSE/Plugins/Baka.json", false)]
    [InlineData("Data/Starfield.esm", false)]
    public void StarfieldNamesTheExtenderAndItsPluginDlls(string path, bool hooks)
        => Game<Starfield>().HooksGameCode(InGame(path)).Should().Be(hooks);

    [Theory]
    [InlineData("OblivionRemastered/Binaries/Win64/obse64_loader.exe", true)]
    [InlineData("OblivionRemastered/Binaries/Win64/obse64_0_2_1.dll", true)]
    [InlineData("OblivionRemastered/Binaries/Win64/OBSE/Plugins/Some.dll", true)]
    [InlineData("OblivionRemastered/Binaries/Win64/ue4ss/UE4SS.dll", false)]
    [InlineData("OblivionRemastered/Content/Paks/Mod_P.pak", false)]
    // The loader only counts beside the game binary.
    [InlineData("obse64_loader.exe", false)]
    public void OblivionRemasteredNamesTheExtenderBesideTheGameBinary(string path, bool hooks)
        => Game<OblivionRemastered>().HooksGameCode(InGame(path)).Should().Be(hooks);

    [Theory]
    [InlineData("red4ext/RED4ext.dll", true)]
    [InlineData("red4ext/plugins/ArchiveXL/ArchiveXL.dll", true)]
    [InlineData("red4ext/plugins/ArchiveXL/ArchiveXL.pdb", false)]
    [InlineData("red4ext/config.toml", false)]
    [InlineData("bin/x64/winmm.dll", true)]
    [InlineData("bin/x64/VERSION.dll", true)]
    [InlineData("bin/x64/d3d11.dll", false)]
    [InlineData("bin/x64/plugins/cyber_engine_tweaks.asi", true)]
    [InlineData("bin/x64/plugins/cyber_engine_tweaks/mods/AMM/init.lua", false)]
    [InlineData("bin/x64/plugins/cyber_engine_tweaks/lib/helper.dll", false)]
    [InlineData("archive/pc/mod/Look.archive", false)]
    [InlineData("r6/scripts/Thing.reds", false)]
    [InlineData("engine/tools/redmod.dll", false)]
    public void CyberpunkNamesTheRed4ExtAndCetFiles(string path, bool hooks)
        => Game<Cyberpunk2077Game>().HooksGameCode(InGame(path)).Should().Be(hooks);

    [Theory]
    [InlineData("BepInEx/plugins/Author-Mod/Mod.dll", true)]
    [InlineData("BepInEx/plugins/Mod.dll", true)]
    [InlineData("BepInEx/plugins/Author-Mod/Mod.cfg", false)]
    [InlineData("BepInEx/config/Mod.cfg", false)]
    // The loader pack is not a mod the user picked.
    [InlineData("winhttp.dll", false)]
    [InlineData("BepInEx/core/BepInEx.dll", false)]
    public void UnityGamesNameBepInExPluginDlls(string path, bool hooks)
        => Game<Peak>().HooksGameCode(InGame(path)).Should().Be(hooks);

    [Fact]
    public async Task AModIsNamedWithItsHookingFilesAndNotItsDataFiles()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "hooks");
        await AddGroup(loadout, "ArchiveXL",
            "red4ext/plugins/ArchiveXL/ArchiveXL.dll", "archive/pc/mod/Look.archive", "red4ext/plugins/ArchiveXL/ArchiveXL.xl");

        var mods = NativeCodeMods.Find(Fresh(loadout));

        var mod = mods.Should().ContainSingle().Subject;
        mod.Name.Should().Be("ArchiveXL");
        mod.Files.Should().Equal(InGame("red4ext/plugins/ArchiveXL/ArchiveXL.dll"));
    }

    [Fact]
    public async Task ModsWithoutHookingFilesAreLeftOut()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "plain");
        await AddGroup(loadout, "Look", "archive/pc/mod/Look.archive");

        NativeCodeMods.Find(Fresh(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ADisabledModIsLeftOut()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "disabled");
        await AddGroup(loadout, "Off", ["red4ext/plugins/Off/Off.dll"], disabled: true);
        await AddGroup(loadout, "On", "red4ext/plugins/On/On.dll");

        NativeCodeMods.Find(Fresh(loadout)).Select(m => m.Name).Should().Equal("On");
    }

    [Fact]
    public async Task AModInADisabledCollectionIsLeftOut()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "collection");
        await AddGroup(loadout, "Inside", ["red4ext/plugins/Inside/Inside.dll"], disabled: false, disabledCollection: true);

        NativeCodeMods.Find(Fresh(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ModsAreListedByNameWithEveryHookingFile()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "several");
        await AddGroup(loadout, "Codeware", "red4ext/plugins/Codeware/Codeware.dll");
        await AddGroup(loadout, "Cyber Engine Tweaks", "bin/x64/plugins/cyber_engine_tweaks.asi", "bin/x64/version.dll", "bin/x64/plugins/cyber_engine_tweaks/mods/x.lua");

        var mods = NativeCodeMods.Find(Fresh(loadout));

        mods.Select(m => m.Name).Should().Equal("Codeware", "Cyber Engine Tweaks");
        mods[1].Files.Should().BeEquivalentTo([InGame("bin/x64/plugins/cyber_engine_tweaks.asi"), InGame("bin/x64/version.dll")]);
    }

    [Fact]
    public async Task AFileTheGameDoesNotNameIsNotCounted()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "other game");
        await AddGroup(loadout, "Skyrim Thing", "Data/SKSE/Plugins/Thing.dll");

        // The seeded loadout is a Cyberpunk one, which has no folder like that.
        NativeCodeMods.Find(Fresh(loadout)).Should().BeEmpty();
        NativeCodeMods.Find(Fresh(loadout), Game<SkyrimSE>()).Should().ContainSingle().Which.Name.Should().Be("Skyrim Thing");
    }

    // The seeded loadout holds the database as it was before the groups were added.
    private Loadout.ReadOnly Fresh(Loadout.ReadOnly loadout) => Loadout.Load(Connection.Db, loadout.Id);

    private async Task AddGroup(Loadout.ReadOnly loadout, string name, params string[] paths)
        => await AddGroup(loadout, name, paths, disabled: false);

    private async Task AddGroup(Loadout.ReadOnly loadout, string name, string[] paths, bool disabled, bool disabledCollection = false)
    {
        using var tx = Connection.BeginTransaction();
        LoadoutItemGroupId? collectionId = null;
        if (disabledCollection)
        {
            _ = new LoadoutItemGroup.New(tx, out var collection)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, collection)
                {
                    LoadoutId = loadout.LoadoutId,
                    Name = "Collection",
                },
            };
            tx.Add(collection, LoadoutItem.Disabled, Null.Instance);
            collectionId = LoadoutItemGroupId.From(collection);
        }

        _ = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId)
            {
                LoadoutId = loadout.LoadoutId,
                Name = name,
            },
        };
        if (collectionId is { } parent) tx.Add(groupId, LoadoutItem.Parent, parent);
        if (disabled) tx.Add(groupId, LoadoutItem.Disabled, Null.Instance);

        foreach (var path in paths)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(path);
            _ = new LoadoutFile.New(tx, out var fileId)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
                {
                    LoadoutItem = new LoadoutItem.New(tx, fileId)
                    {
                        LoadoutId = loadout.LoadoutId,
                        ParentId = LoadoutItemGroupId.From(groupId),
                        Name = path,
                    },
                    TargetPath = InGame(path).ToGamePathParentTuple(loadout.LoadoutId),
                },
                Hash = bytes.xxHash3(),
                Size = Size.FromLong(bytes.Length),
            };
        }

        await tx.Commit();
    }
}
