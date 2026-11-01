using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
    public void TheXEditFamilyIsRegisteredOnceEach()
    {
        provider.GetServices<ToolDefinition>().Select(d => d.Id?.Value).Should().BeEquivalentTo("xedit", "xlodgen", "texgen", "dyndolod");
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
