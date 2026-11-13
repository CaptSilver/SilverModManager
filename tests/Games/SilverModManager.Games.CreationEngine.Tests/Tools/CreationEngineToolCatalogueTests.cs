using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileSystemGlobbing;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Tools;

/// <summary>
/// The xEdit family the Creation Engine project registers: which games each tool applies to, the file names it is
/// found by, where its output goes and what its Wine prefix needs.
/// </summary>
public class CreationEngineToolCatalogueTests(IServiceProvider provider)
{
    private ToolDefinition Definition(string id) =>
        provider.GetServices<ToolDefinition>().Single(d => d.Id == new ToolId(id));

    private IGame Game(GameId id) => provider.GetServices<IGame>().Single(g => g.GameId == id);

    [Fact]
    public void EachCatalogueToolIsRegisteredOnce()
    {
        provider.GetServices<ToolDefinition>().Select(d => d.Id?.Value).Should().BeEquivalentTo("xedit", "xlodgen", "texgen", "dyndolod", "pandora", "nemesis", "bodyslide", "outfit-studio", "oc-unleashed-configurator");
    }

    [Fact]
    public void TheOpenCompositeUnleashedConfiguratorIsFoundByItsExeNameForSkyrimVrOnly()
    {
        var configurator = Definition("oc-unleashed-configurator");
        var skyrimVr = Game(global::SilverModManager.Games.CreationEngine.SkyrimVR.SkyrimVR.GameId);

        configurator.ExecutableNames(skyrimVr).Should().Equal("OC Unleashed Configurator for Skyrim VR.exe");
        configurator.AppliesTo(skyrimVr).Should().BeTrue();
        configurator.AppliesTo(Game(global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId)).Should().BeFalse();
        configurator.AppliesTo(Game(global::SilverModManager.Games.CreationEngine.Fallout4VR.Fallout4VR.GameId)).Should().BeFalse();
    }

    [Fact]
    public void TheConfiguratorTakesNoArgumentsAndNeedsNoPrefixSetup()
    {
        var configurator = Definition("oc-unleashed-configurator");

        configurator.Arguments.Words.Should().BeEmpty();
        configurator.Prefix.Should().Be(ToolPrefixRequirements.None);
        configurator.ExitCodes.Should().Be(ExitCodePolicy.AnyExitIsFine);
    }

    [Fact]
    public void TheOcuConfiguratorKeepsItsOutput()
    {
        var configurator = Definition("oc-unleashed-configurator");

        configurator.Output.Should().Be(new ToolOutput.GameFolder("OpenComposite Unleashed Configurator output"));
        configurator.IgnoredOutput.Should().Contain("**/*.log");
        configurator.IgnoredOutput.Should().Contain("**/*.log?");
    }

    [Theory]
    [InlineData("Skyrim Special Edition", "SSEEdit.exe", "SSEEdit64.exe")]
    [InlineData("Fallout 4", "FO4Edit.exe", "FO4Edit64.exe")]
    [InlineData("Fallout: New Vegas", "FNVEdit.exe", "FNVEdit64.exe")]
    [InlineData("Oblivion", "TES4Edit.exe", "TES4Edit64.exe")]
    public void XEditIsFoundByTheGamesOwnNamesAndTheGenericOnes(string displayName, params string[] own)
    {
        var game = provider.GetServices<IGame>().Single(g => g.DisplayName == displayName);

        Definition("xedit").ExecutableNames(game).Should().BeEquivalentTo([.. own, "xEdit.exe", "xEdit64.exe"]);
    }

    [Fact]
    public void XEditAppliesToACreationEngineGameAndNotToOthers()
    {
        var xedit = Definition("xedit");
        var other = provider.GetServices<IGame>().First(g => g is not global::SilverModManager.Games.CreationEngine.Abstractions.ICreationEngineGame);

        xedit.AppliesTo(Game(global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId)).Should().BeTrue();
        xedit.AppliesTo(other).Should().BeFalse();
    }

    [Fact]
    public void XEditArgumentsNameTheDataFolderIniAndPluginsFile()
    {
        Definition("xedit").Arguments.Words.Should().Equal("-D:{DataFolder}", "-I:{IniFile}", "-P:{PluginsFile}");
    }

    [Fact]
    public void XEditAddsTheGameSwitchForAGenericExe()
    {
        var xedit = Definition("xedit");

        xedit.ExtraArguments!(Game(global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId), "xEdit64.exe").Should().Equal("-sse");
        xedit.ExtraArguments!(Game(global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId), "SSEEdit64.exe").Should().BeEmpty();
    }

    [Fact]
    public void XEditKeepsWhatItSavesInPlaceAndExitsFreely()
    {
        var xedit = Definition("xedit");

        xedit.Output.Should().Be(new ToolOutput.GameFolder("xEdit Output"));
        xedit.ExitCodes.Should().Be(ExitCodePolicy.AnyExitIsFine);
    }

    [Fact]
    public void QuickAutoCleanCleansOnePickedPluginAndMustExitCleanly()
    {
        var clean = Definition("xedit").QuickAutoClean;

        clean.Should().NotBeNull();
        clean!.Arguments.Words.TakeLast(5).Should().Equal("-quickautoclean", "-autoload", "-autoexit", "-IKnowWhatImDoing", "{Plugin}");
        clean.Arguments.Words.Should().Contain("-D:{DataFolder}");
        clean.ExitCodes.Should().Be(ExitCodePolicy.ZeroIsSuccess);
    }

    [Theory]
    [InlineData("xlodgen", "xLODGen Output")]
    [InlineData("texgen", "TexGen Output")]
    [InlineData("dyndolod", "DynDOLOD Output")]
    public void TheLodToolsWriteToAFolderThatBecomesTheirGroupUnderData(string id, string group)
    {
        Definition(id).Output.Should().Be(new ToolOutput.DeclaredFolder(group, new GamePath(LocationId.Game, "Data")));
        Definition(id).Arguments.Words.Should().Contain("-O:{OutputFolder}");
    }

    [Fact]
    public void TheLodToolsAreFoundByTheirOwnNames()
    {
        var skyrim = Game(global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId);

        Definition("xlodgen").ExecutableNames(skyrim).Should().BeEquivalentTo("xLODGen.exe", "xLODGenx64.exe");
        Definition("texgen").ExecutableNames(skyrim).Should().BeEquivalentTo("TexGen.exe", "TexGenx64.exe");
        Definition("dyndolod").ExecutableNames(skyrim).Should().BeEquivalentTo("DynDOLOD.exe", "DynDOLODx64.exe");
    }

    [Theory]
    [InlineData("texgen")]
    [InlineData("dyndolod")]
    public void TexGenAndDynDolodApplyToSkyrimAndFallout4Only(string id)
    {
        var definition = Definition(id);

        foreach (var applies in new[] { global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId, global::SilverModManager.Games.CreationEngine.SkyrimLE.SkyrimLE.GameId, global::SilverModManager.Games.CreationEngine.SkyrimVR.SkyrimVR.GameId, global::SilverModManager.Games.CreationEngine.EnderalSE.EnderalSE.GameId, global::SilverModManager.Games.CreationEngine.Fallout4.Fallout4.GameId })
            definition.AppliesTo(Game(applies)).Should().BeTrue(applies.ToString());
        foreach (var not in new[] { global::SilverModManager.Games.CreationEngine.Fallout3.Fallout3.GameId, global::SilverModManager.Games.CreationEngine.Starfield.Starfield.GameId, global::SilverModManager.Games.CreationEngine.Morrowind.Morrowind.GameId })
            definition.AppliesTo(Game(not)).Should().BeFalse(not.ToString());
    }

    [Fact]
    public void XLodGenSkipsGamesWhoseDataFolderIsNotData()
    {
        var xlodgen = Definition("xlodgen");

        xlodgen.AppliesTo(Game(global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId)).Should().BeTrue();
        xlodgen.AppliesTo(Game(global::SilverModManager.Games.CreationEngine.Morrowind.Morrowind.GameId)).Should().BeFalse();
        xlodgen.AppliesTo(Game(global::SilverModManager.Games.CreationEngine.OblivionRemastered.OblivionRemastered.GameId)).Should().BeFalse();
    }

    [Theory]
    [InlineData("xedit", "SSEEdit64.exe")]
    [InlineData("xlodgen", "xLODGenx64.exe")]
    [InlineData("texgen", "TexGenx64.exe")]
    [InlineData("dyndolod", "DynDOLODx64.exe")]
    public void EveryToolNeedsTheVisualCppRuntimeAndRunsAsWinXp(string id, string exe)
    {
        var prefix = Definition(id).Prefix;

        prefix.Components.Should().Equal(PrefixComponents.VisualCppRuntime.Id);
        prefix.RegistryDefaults.Should().Contain(new PrefixRegistryDefault(exe, "Version", "winxp"));
    }

    [Fact]
    public void TheLodToolsAddTheGameSwitchToo()
    {
        var skyrim = Game(global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId);

        Definition("dyndolod").ExtraArguments!(skyrim, "DynDOLODx64.exe").Should().Equal("-sse");
    }

    [Theory]
    [InlineData("pandora", "Pandora Behaviour Engine+.exe")]
    [InlineData("nemesis", "Nemesis Unlimited Behavior Engine.exe")]
    public void TheBehaviourEnginesAreFoundByTheirOwnExeName(string id, string exe)
    {
        Definition(id).ExecutableNames(Game(global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId)).Should().Equal(exe);
    }

    [Fact]
    public void BodySlideIsFoundByItsTwoExeNamesForEveryGameItAppliesTo()
    {
        var names = new[] { "BodySlide x64.exe", "BodySlide.exe" };

        Definition("bodyslide").ExecutableNames(Game(global::SilverModManager.Games.CreationEngine.Fallout4.Fallout4.GameId)).Should().Equal(names);
        Definition("bodyslide").ExecutableNames(Game(global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId)).Should().Equal(names);
    }

    // Outfit Studio ships beside BodySlide in the same folder; as its own entry, a scan of that folder saves it
    // under its own name instead of a second "BodySlide".
    [Fact]
    public void OutfitStudioIsItsOwnToolForTheSameGamesAsBodySlide()
    {
        var outfitStudio = Definition("outfit-studio");

        outfitStudio.DisplayName.Should().Be("Outfit Studio");
        outfitStudio.ExecutableNames(Game(global::SilverModManager.Games.CreationEngine.Fallout4.Fallout4.GameId)).Should().Equal("Outfit Studio x64.exe");
        AppliesTo("outfit-studio").Should().BeEquivalentTo(AppliesTo("bodyslide"));
    }

    [Fact]
    public void PandoraAppliesToSkyrimSeAndVrOnly()
    {
        AppliesTo("pandora").Should().BeEquivalentTo(new[] { global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId, global::SilverModManager.Games.CreationEngine.SkyrimVR.SkyrimVR.GameId });
    }

    [Fact]
    public void NemesisAppliesToSkyrimSeLeAndVr()
    {
        AppliesTo("nemesis").Should().BeEquivalentTo(new[] { global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId, global::SilverModManager.Games.CreationEngine.SkyrimLE.SkyrimLE.GameId, global::SilverModManager.Games.CreationEngine.SkyrimVR.SkyrimVR.GameId });
    }

    [Fact]
    public void BodySlideAppliesToSkyrimSeLeVrAndFallout4WithItsVrBuild()
    {
        AppliesTo("bodyslide").Should().BeEquivalentTo(new[] { global::SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE.GameId, global::SilverModManager.Games.CreationEngine.SkyrimLE.SkyrimLE.GameId, global::SilverModManager.Games.CreationEngine.SkyrimVR.SkyrimVR.GameId, global::SilverModManager.Games.CreationEngine.Fallout4.Fallout4.GameId, global::SilverModManager.Games.CreationEngine.Fallout4VR.Fallout4VR.GameId });
    }

    private IEnumerable<GameId> AppliesTo(string id)
    {
        var definition = Definition(id);
        return provider.GetServices<IGame>().Where(definition.AppliesTo).Select(g => g.GameId);
    }

    [Fact]
    public void PandoraNeedsTheDotNet8DesktopRuntimeAndNoWindowDecorations()
    {
        var prefix = Definition("pandora").Prefix;

        prefix.Components.Should().Equal(PrefixComponents.DotNetDesktop8.Id);
        prefix.RegistryDefaults.Should().Equal(new PrefixRegistryDefault("Pandora Behaviour Engine+.exe", "Decorated", "N"));
    }

    [Fact]
    public void EveryComponentAToolRequiresResolvesThroughThePrefixComponentTable()
    {
        foreach (var id in provider.GetServices<ToolDefinition>().SelectMany(d => d.Prefix.Components))
            PrefixComponents.TryResolve(id.Value, out _).Should().BeTrue(id.Value);
    }

    [Theory]
    [InlineData("nemesis")]
    [InlineData("bodyslide")]
    [InlineData("outfit-studio")]
    public void NemesisAndBodySlideNeedTheVisualCppRuntimeOnly(string id)
    {
        var prefix = Definition(id).Prefix;

        prefix.Components.Should().Equal(PrefixComponents.VisualCppRuntime.Id);
        prefix.RegistryDefaults.Should().BeEmpty();
    }

    [Theory]
    [InlineData("pandora", "Pandora Output")]
    [InlineData("nemesis", "Nemesis Output")]
    [InlineData("bodyslide", "BodySlide Output")]
    [InlineData("outfit-studio", "Outfit Studio Output")]
    public void TheseToolsWriteIntoTheGameFolderAndTakeNoArguments(string id, string group)
    {
        var definition = Definition(id);

        definition.Output.Should().Be(new ToolOutput.GameFolder(group));
        definition.Arguments.Words.Should().BeEmpty();
        definition.ExitCodes.Should().Be(ExitCodePolicy.AnyExitIsFine);
    }

    // Pandora writes its working set, log and crash logs into its output folder (Data by default) and Settings.json
    // beside its exe; none of that is behaviour output.
    [Fact]
    public void PandoraKeepsItsWorkingSetLogsAndSettingsOutOfTheGroup()
    {
        Definition("pandora").IgnoredOutput.Should().Equal(
            "Data/Pandora_Engine/**",
            "Data/Engine.log",
            "Data/Pandora_CriticalCrash_*.log",
            "Data/Settings.json");
    }

    // The cache, logs and ini are written relative to the working directory, which Nemesis requires to be Data/Nemesis_Engine.
    [Fact]
    public void NemesisKeepsItsCacheLogsAndSettingsOutOfTheGroup()
    {
        Definition("nemesis").IgnoredOutput.Should().Equal(
            "Data/Nemesis_Engine/cache/**",
            "Data/Nemesis_Engine/temp_behaviors/**",
            "Data/Nemesis_Engine/nemesis.ini",
            "Data/Nemesis_Engine/CriticalLog.txt",
            "Data/Nemesis_Engine/UpdateLog.txt",
            "Data/Nemesis_Engine/PatchLog.txt");
    }

    // Both programs save their settings and logs beside their exe (BodySlideApp.cpp, OutfitStudio.cpp).
    [Theory]
    [InlineData("bodyslide")]
    [InlineData("outfit-studio")]
    public void BodySlideAndOutfitStudioKeepTheirSettingsAndLogsOutOfTheGroup(string id)
    {
        Definition(id).IgnoredOutput.Should().Equal(
            "Data/CalienteTools/BodySlide/Config.xml",
            "Data/CalienteTools/BodySlide/BodySlide.xml",
            "Data/CalienteTools/BodySlide/OutfitStudio.xml",
            "Data/CalienteTools/BodySlide/AnimationFavorites.xml",
            "Data/CalienteTools/BodySlide/Log_BS.txt",
            "Data/CalienteTools/BodySlide/Log_OS.txt");
    }

    // Matched the way the run's capture matches them: everything except the ignored globs, ignoring case.
    [Theory]
    [InlineData("pandora", "Data/meshes/actors/character/behaviors/0_master.hkx", "Data/Pandora_Engine/PreviousOutput.txt")]
    [InlineData("pandora", "Data/meshes/actors/character/_1stperson/characters/firstperson.hkx", "Data/pandora_criticalcrash_uithread.log")]
    [InlineData("nemesis", "Data/meshes/actors/character/behaviors/0_master.hkx", "Data/Nemesis_Engine/cache/animationdata_list")]
    [InlineData("nemesis", "Data/Nemesis_PCEA.esp", "Data/Nemesis_Engine/CriticalLog.txt")]
    [InlineData("bodyslide", "Data/meshes/armor/daedric/f/cuirass_1.nif", "Data/CalienteTools/BodySlide/Config.xml")]
    [InlineData("outfit-studio", "Data/CalienteTools/BodySlide/ShapeData/MyOutfit/MyOutfit.osp", "Data/CalienteTools/BodySlide/Log_OS.txt")]
    public void TheIgnoredGlobsDropBookkeepingButKeepWhatTheGameLoads(string id, string kept, string dropped)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude("**/*");
        foreach (var glob in Definition(id).IgnoredOutput) matcher.AddExclude(glob);

        matcher.Match(kept).HasMatches.Should().BeTrue();
        matcher.Match(dropped).HasMatches.Should().BeFalse();
    }

    [Fact]
    public void TheLodPipelineRunsTerrainThenTexturesThenObjectsAndNamesOnlyRegisteredTools()
    {
        var pipeline = provider.GetServices<ToolPipeline>().Should().ContainSingle().Subject;

        pipeline.Name.Should().Be("LOD generation");
        pipeline.Steps.Select(s => s.Value).Should().Equal("xlodgen", "texgen", "dyndolod");
        var registered = provider.GetServices<ToolDefinition>().Select(d => d.Id).ToList();
        pipeline.Steps.Should().OnlyContain(step => registered.Contains(step));
    }
}
