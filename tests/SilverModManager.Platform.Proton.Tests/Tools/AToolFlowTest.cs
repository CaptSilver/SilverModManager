using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.DataModel;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Platform.Proton.Tests.Tools;

/// <summary>A one-game fixture for the tests of what the user does with tools: add them, run them, chain them.</summary>
public abstract class AToolFlowTest<TSelf>(ITestOutputHelper helper) : AIsolatedDefinedGameTest<TSelf>(helper, Definition.GameId)
    where TSelf : AToolFlowTest<TSelf>
{
    protected static readonly SimpleGameDefinition Definition = new()
    {
        GameId = GameId.From("Tests.ToolFlow"),
        DisplayName = "Tool Flow Game",
        NexusModsGameId = Optional<Sdk.NexusModsApi.NexusModsGameId>.None,
        Stores = new StoreIdentifiers(GameId.From("Tests.ToolFlow")),
        Executable = "bin/game.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "My Mods"),
        Install = new SimpleInstallRules(),
    };

    protected static ToolDefinition Catalogue(string id, string display, params string[] exeNames) => new()
    {
        Id = new ToolId(id),
        DisplayName = display,
        AppliesTo = _ => true,
        ExecutableNames = _ => exeNames,
        Arguments = ArgumentTemplate.Empty,
        Output = ToolOutput.None,
    };

    protected static readonly ToolDefinition TexGen = Catalogue("texgen", "TexGen", "TexGen.exe", "TexGenx64.exe");
    protected static readonly ToolDefinition DynDolod = Catalogue("dyndolod", "DynDOLOD", "DynDOLOD.exe", "DynDOLODx64.exe");

    protected readonly IToolRunner ToolRunner = Substitute.For<IToolRunner>();
    protected readonly IProcessRunner ProcessRunner = Substitute.For<IProcessRunner>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddDefinedGame(Definition, (provider, definition) => new SimpleGame(provider, definition))
            .AddUniversalGameLocator(Definition.GameId, locationsFileSystem: IsolatedKnownFolders.Create);
    }

    protected ModdingToolFactory ToolFactory()
    {
        var prefixResolver = Substitute.For<IPrefixResolver>();
        return new ModdingToolFactory(ToolRunner, ProcessRunner, new ToolArgumentResolver(), prefixResolver,
            ServiceProvider.GetRequiredService<ToolOutputCapture>(), new ToolPrefixSetup(ToolRunner, prefixResolver, new FakePrefixInspector()));
    }
}
