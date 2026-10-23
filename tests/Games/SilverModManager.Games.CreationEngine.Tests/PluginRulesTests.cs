using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.Bethesda.LoadOrder.Tests;
using NexusMods.Games.Bethesda.Loot.Masterlists;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>The per-title facts the load order rules read: header layout, list mechanism, hardcoded plugins and flag rules.</summary>
public class PluginRulesTests(IServiceProvider serviceProvider)
{
    private static readonly string[] SkyrimSeHardcoded = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    private static readonly string[] Fallout4Hardcoded =
    [
        "Fallout4.esm", "DLCRobot.esm", "DLCworkshop01.esm", "DLCCoast.esm", "DLCworkshop02.esm",
        "DLCworkshop03.esm", "DLCNukaWorld.esm", "DLCUltraHighResolution.esm",
    ];

    private static readonly string[] StarfieldHardcoded =
    [
        "Starfield.esm", "Constellation.esm", "OldMars.esm", "ShatteredSpace.esm", "SFBGS003.esm", "SFBGS004.esm",
        "SFBGS006.esm", "SFBGS007.esm", "SFBGS008.esm", "SFBGS00D.esm", "SFBGS047.esm", "SFBGS050.esm",
    ];

    private static readonly Extension[] WithoutLight = [KnownCEExtensions.ESM, KnownCEExtensions.ESP];
    private static readonly Extension[] WithLight = [KnownCEExtensions.ESM, KnownCEExtensions.ESP, KnownCEExtensions.ESL];

    public static TheoryData<Type, Type, string[], bool, bool> Titles => new()
    {
        { typeof(CreationEngine.SkyrimLE.SkyrimLE), typeof(TextfileMechanism), ["Skyrim.esm", "Update.esm"], false, false },
        { typeof(CreationEngine.Enderal.Enderal), typeof(TextfileMechanism), ["Skyrim.esm", "Update.esm"], false, false },
        { typeof(CreationEngine.SkyrimSE.SkyrimSE), typeof(AsteriskMechanism), SkyrimSeHardcoded, true, true },
        { typeof(CreationEngine.EnderalSE.EnderalSE), typeof(AsteriskMechanism), SkyrimSeHardcoded, true, true },
        { typeof(CreationEngine.SkyrimVR.SkyrimVR), typeof(AsteriskMechanism), [.. SkyrimSeHardcoded, "SkyrimVR.esm"], true, true },
        { typeof(CreationEngine.Fallout4.Fallout4), typeof(AsteriskMechanism), Fallout4Hardcoded, true, true },
        { typeof(CreationEngine.Fallout4VR.Fallout4VR), typeof(AsteriskMechanism), ["Fallout4.esm", "Fallout4_VR.esm"], true, true },
        { typeof(CreationEngine.Starfield.Starfield), typeof(AsteriskMechanism), StarfieldHardcoded, true, true },
    };

    private ICreationEngineGame Registered(Type type) =>
        serviceProvider.GetServices<IGame>().OfType<ICreationEngineGame>().Single(g => g.GetType() == type);

    [Theory]
    [MemberData(nameof(Titles))]
    public void EachTitleDeclaresItsOwnRules(Type type, Type mechanism, string[] hardcoded, bool light, bool masterByExtension)
    {
        var game = Registered(type);

        game.HeaderFormat.Should().Be(PluginFormat.Tes4);
        // These titles pick their mechanism without looking at the install.
        game.PluginList(null!).Should().BeOfType(mechanism);
        game.HardcodedPlugins.Should().Equal(hardcoded);
        game.PluginExtensions.Should().Equal(light ? WithLight : WithoutLight);
        game.SupportsLightPlugins.Should().Be(light);
        game.MasterByExtension.Should().Be(masterByExtension);
        game.MasterFlagIgnored(null!).Should().BeFalse();
    }

    [Theory]
    [InlineData(typeof(CreationEngine.SkyrimLE.SkyrimLE), LootGame.Skyrim)]
    [InlineData(typeof(CreationEngine.SkyrimSE.SkyrimSE), LootGame.SkyrimSE)]
    [InlineData(typeof(CreationEngine.SkyrimVR.SkyrimVR), LootGame.SkyrimVR)]
    [InlineData(typeof(CreationEngine.Enderal.Enderal), LootGame.Enderal)]
    [InlineData(typeof(CreationEngine.EnderalSE.EnderalSE), LootGame.EnderalSE)]
    [InlineData(typeof(CreationEngine.Fallout3.Fallout3), LootGame.Fallout3)]
    [InlineData(typeof(CreationEngine.FalloutNewVegas.FalloutNewVegas), LootGame.FalloutNV)]
    [InlineData(typeof(CreationEngine.Fallout4.Fallout4), LootGame.Fallout4)]
    [InlineData(typeof(CreationEngine.Fallout4VR.Fallout4VR), LootGame.Fallout4VR)]
    [InlineData(typeof(CreationEngine.Starfield.Starfield), LootGame.Starfield)]
    [InlineData(typeof(CreationEngine.Oblivion.Oblivion), LootGame.Oblivion)]
    [InlineData(typeof(CreationEngine.OblivionRemastered.OblivionRemastered), LootGame.OblivionRemastered)]
    public void EachTitleSortsWithItsOwnLootGame(Type type, LootGame expected) =>
        Registered(type).LootGame(null!).Should().Be(expected);

    [Theory]
    [InlineData(typeof(CreationEngine.SkyrimSE.SkyrimSE), "Skyrim.ccc")]
    [InlineData(typeof(CreationEngine.Fallout4.Fallout4), "Fallout4.ccc")]
    public void CreationClubListLivesInTheGameFolder(Type type, string file) =>
        Registered(type).CreationClubListFile.Should().Be(new GamePath(LocationId.Game, file));

    [Theory]
    [InlineData(typeof(CreationEngine.SkyrimLE.SkyrimLE))]
    [InlineData(typeof(CreationEngine.Enderal.Enderal))]
    [InlineData(typeof(CreationEngine.SkyrimVR.SkyrimVR))]
    [InlineData(typeof(CreationEngine.Fallout4VR.Fallout4VR))]
    public void TitlesWithoutACreationClubHaveNoListFile(Type type) =>
        Registered(type).CreationClubListFile.Should().BeNull();

    [Fact]
    public void StarfieldReadsItsCreationClubListFromMyGamesBeforeTheGameFolder() =>
        Registered(typeof(CreationEngine.Starfield.Starfield)).CreationClubListFile
            .Should().Be(new GamePath(LocationId.Preferences, "Starfield.ccc"));

    private static PluginHeader Header(string name, uint flags = 0, params string[] masters) =>
        new(name, masters, flags, null, null, null, null, PluginFormat.Tes4);

    [Fact]
    public void EslIsAMasterOnSkyrimSe_ButNotOnSkyrimLe()
    {
        var esl = Header("A.esl");

        Registered(typeof(CreationEngine.SkyrimSE.SkyrimSE)).IsMaster(esl, null!).Should().BeTrue();
        Registered(typeof(CreationEngine.SkyrimLE.SkyrimLE)).IsMaster(esl, null!).Should().BeFalse();
    }

    [Fact]
    public void EsmIsAMasterOnSkyrimSeWithoutTheFlag_ButNotOnSkyrimLe()
    {
        var esm = Header("A.esm");

        Registered(typeof(CreationEngine.SkyrimSE.SkyrimSE)).IsMaster(esm, null!).Should().BeTrue();
        Registered(typeof(CreationEngine.SkyrimLE.SkyrimLE)).IsMaster(esm, null!).Should().BeFalse();
    }

    [Fact]
    public void MasterFlagMakesAMasterOnSkyrimLe() =>
        Registered(typeof(CreationEngine.SkyrimLE.SkyrimLE)).IsMaster(Header("A.esp", 1), null!).Should().BeTrue();

    [Fact]
    public void LightFlagIsOnlyReadWhereTheGameSupportsLightPlugins()
    {
        var light = Header("A.esp", 0x200);

        Registered(typeof(CreationEngine.SkyrimSE.SkyrimSE)).IsLight(light).Should().BeTrue();
        Registered(typeof(CreationEngine.SkyrimLE.SkyrimLE)).IsLight(light).Should().BeFalse();
    }

    // Starfield's flag layout differs from the other titles': light is 0x100, update 0x200, medium 0x400, blueprint 0x800.
    private static PluginHeader StarfieldHeader(uint flags, params string[] masters)
    {
        var bytes = PluginBytes.Write(PluginFormat.Tes4, new PluginSpec { RecordFlags = flags, Masters = masters });
        return PluginHeaderReader.Read(new MemoryStream(bytes), PluginFormat.Tes4, "Plugin.esm");
    }

    [Fact]
    public void StarfieldLightFlagIs0x100()
    {
        var starfield = Registered(typeof(CreationEngine.Starfield.Starfield));

        starfield.IsLight(StarfieldHeader(0x100)).Should().BeTrue();
        starfield.IsLight(StarfieldHeader(0x200)).Should().BeFalse("0x200 is the update flag on Starfield");
    }

    [Fact]
    public void StarfieldMediumFlagIs0x400()
    {
        var starfield = Registered(typeof(CreationEngine.Starfield.Starfield));

        starfield.IsMedium(StarfieldHeader(0x400)).Should().BeTrue();
        starfield.IsMedium(StarfieldHeader(0x400 | 0x100)).Should().BeFalse("the light flag wins over the medium flag");
    }

    [Fact]
    public void StarfieldUpdatePluginNeedsTheFlagAndAMaster()
    {
        var starfield = Registered(typeof(CreationEngine.Starfield.Starfield));

        starfield.IsUpdate(StarfieldHeader(0x200, "Starfield.esm")).Should().BeTrue();
        starfield.IsUpdate(StarfieldHeader(0x200)).Should().BeFalse("an update plugin with no masters is not one");
    }

    [Fact]
    public void StarfieldBlueprintFlagIs0x800()
    {
        Registered(typeof(CreationEngine.Starfield.Starfield)).IsBlueprint(StarfieldHeader(0x800)).Should().BeTrue();
        Registered(typeof(CreationEngine.Starfield.Starfield)).IsBlueprint(StarfieldHeader(0)).Should().BeFalse();
    }

    [Fact]
    public void OtherTitlesHaveNoMediumUpdateOrBlueprintPlugins()
    {
        var skyrim = Registered(typeof(CreationEngine.SkyrimSE.SkyrimSE));
        var header = Header("A.esp", 0xE00, "Skyrim.esm");

        skyrim.IsMedium(header).Should().BeFalse();
        skyrim.IsUpdate(header).Should().BeFalse();
        skyrim.IsBlueprint(header).Should().BeFalse();
    }

    // A loadout that is not valid has no installation to pick a mechanism from. The synchronizer only asks with a real one.
    [Theory]
    [InlineData(typeof(CreationEngine.SkyrimSE.SkyrimSE))]
    [InlineData(typeof(CreationEngine.Starfield.Starfield))]
    public void ALoadoutThatIsNotValidIsOfferedNoPluginList(Type type) =>
        ((ALoadoutSynchronizer)((IGame)Registered(type)).Synchronizer).IntrinsicFiles(default).Should().BeEmpty();
}
