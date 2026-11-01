using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Platform.Proton.UserTools;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Platform.Proton.Tests.Tools;

/// <summary>
/// A tool row with a catalogue id lists under the games its definition applies to, and only there; a row
/// without one stays the plain program it always was.
/// </summary>
public class CatalogueToolProviderTests(ITestOutputHelper helper)
    : AIsolatedDefinedGameTest<CatalogueToolProviderTests>(helper, Definition.GameId)
{
    private static readonly SimpleGameDefinition Definition = new()
    {
        GameId = GameId.From("Tests.CatalogueTools"),
        DisplayName = "Catalogue Tools Game",
        NexusModsGameId = Optional<Sdk.NexusModsApi.NexusModsGameId>.None,
        Stores = new StoreIdentifiers(GameId.From("Tests.CatalogueTools")),
        Executable = "bin/game.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "My Mods"),
        Install = new SimpleInstallRules(),
    };

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddDefinedGame(Definition, (provider, definition) => new SimpleGame(provider, definition))
            .AddUniversalGameLocator(Definition.GameId, locationsFileSystem: IsolatedKnownFolders.Create);
    }

    private readonly IToolRunner _runner = Substitute.For<IToolRunner>();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly List<CliWrap.Command> _commands = [];

    private static ToolDefinition Catalogue(string id, string display, Func<IGame, bool> appliesTo, ToolOutput? output = null) => new()
    {
        Id = new ToolId(id),
        DisplayName = display,
        AppliesTo = appliesTo,
        ExecutableNames = _ => [id + ".exe"],
        Arguments = ArgumentTemplate.Empty,
        Output = output ?? ToolOutput.None,
    };

    private CatalogueToolProvider CatalogueProvider(params ToolDefinition[] definitions)
        => new(Connection, ModdingToolFactory(), definitions);

    private UserToolProvider UserProvider() => new(Connection, ModdingToolFactory());

    private ModdingToolFactory ModdingToolFactory()
    {
        var runner = _runner;
        var prefixResolver = Substitute.For<IPrefixResolver>();
        return new ModdingToolFactory(runner, _processRunner, new ToolArgumentResolver(), prefixResolver,
            ServiceProvider.GetRequiredService<ToolOutputCapture>(), new ToolPrefixSetup(runner, prefixResolver, new FakePrefixInspector()));
    }

    private async Task AddRow(Loadout.ReadOnly loadout, string name, string exe, string? toolId = null, string? outputGroupName = null)
    {
        using var tx = Connection.BeginTransaction();
        var row = new UserTool.New(tx)
        {
            InstallationId = loadout.InstallationId,
            Name = name,
            Exe = exe,
            Arguments = "",
            RunVia = ToolRunVia.Auto,
        };
        if (toolId is not null) tx.Add(row.Id, UserTool.ToolId, toolId);
        if (outputGroupName is not null) tx.Add(row.Id, UserTool.OutputGroupName, outputGroupName);
        await tx.Commit();
    }

    [Fact]
    public async Task ARowWithAToolIdListsUnderItsDefinitionsName()
    {
        var loadout = await CreateLoadout();
        await AddRow(loadout, "saved name", "/tools/dyndolod.exe", toolId: "dyndolod");

        var tools = CatalogueProvider(Catalogue("dyndolod", "DynDOLOD", _ => true)).GetTools(loadout).ToArray();

        var tool = tools.Should().ContainSingle().Which.Should().BeOfType<ModdingTool>().Subject;
        tool.Name.Should().Be("DynDOLOD");
        tool.Exe.Should().Be(FileSystem.FromUnsanitizedFullPath("/tools/dyndolod.exe"));
        tool.GameIds.Should().Equal(Definition.GameId);
    }

    [Fact]
    public async Task ARowListsNothingForAGameItsDefinitionDoesNotApplyTo()
    {
        var loadout = await CreateLoadout();
        await AddRow(loadout, "TexGen", "/tools/texgen.exe", toolId: "texgen");

        var forOtherGame = Catalogue("texgen", "TexGen", game => game.GameId == GameId.From("Tests.Other"));

        CatalogueProvider(forOtherGame).GetTools(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task ARowWhoseDefinitionIsNotRegisteredIsSkippedNotCrashed()
    {
        var loadout = await CreateLoadout();
        await AddRow(loadout, "Gone", "/tools/gone.exe", toolId: "removed-from-catalogue");

        CatalogueProvider(Catalogue("dyndolod", "DynDOLOD", _ => true)).GetTools(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyRowsWithAToolIdGoThroughTheCatalogueAndOnlyRowsWithoutOneThroughTheUserProvider()
    {
        var loadout = await CreateLoadout();
        await AddRow(loadout, "Mine", "/tools/mine.exe");
        await AddRow(loadout, "DynDOLOD", "/tools/dyndolod.exe", toolId: "dyndolod");

        CatalogueProvider(Catalogue("dyndolod", "DynDOLOD", _ => true)).GetTools(loadout).Select(t => t.Name).Should().Equal("DynDOLOD");
        UserProvider().GetTools(loadout).Select(t => t.Name).Should().Equal("Mine");
    }

    [Fact]
    public async Task AnOutputGroupNameOnTheRowRenamesTheGroupTheToolCapturesInto()
    {
        var loadout = await CreateLoadout();
        await AddRow(loadout, "DynDOLOD", "/tools/dyndolod.exe", toolId: "dyndolod", outputGroupName: "Winter LOD");
        await AddRow(loadout, "TexGen", "/tools/texgen.exe", toolId: "texgen");
        var mapsTo = new GamePath(LocationId.Game, "My Mods");

        var tools = CatalogueProvider(
                Catalogue("dyndolod", "DynDOLOD", _ => true, new ToolOutput.DeclaredFolder("DynDOLOD Output", mapsTo)),
                Catalogue("texgen", "TexGen", _ => true, new ToolOutput.DeclaredFolder("TexGen Output", mapsTo)))
            .GetTools(loadout).Cast<IToolWithOutput>().ToDictionary(t => ((ITool)t).Name, t => t.Output);

        tools["DynDOLOD"].Should().Be(new ToolOutput.DeclaredFolder("Winter LOD", mapsTo));
        tools["TexGen"].Should().Be(new ToolOutput.DeclaredFolder("TexGen Output", mapsTo));
    }

    [Fact]
    public async Task AGameFolderToolsGroupIsRenamedToo()
    {
        var loadout = await CreateLoadout();
        await AddRow(loadout, "Pandora", "/tools/pandora.exe", toolId: "pandora", outputGroupName: "My Behaviours");

        var tool = CatalogueProvider(Catalogue("pandora", "Pandora", _ => true, new ToolOutput.GameFolder("Pandora Output")))
            .GetTools(loadout).Should().ContainSingle().Which.Should().BeAssignableTo<IToolWithOutput>().Subject;

        tool.Output.Should().Be(new ToolOutput.GameFolder("My Behaviours"));
    }

    [Fact]
    public async Task ASavedRowWithBlankArgumentsRunsWithTheDefinitionsArguments()
    {
        _runner.RunWindowsTool(default!, default, default!, default, default, default, default)
            .ReturnsForAnyArgs(_ => new ToolRunResult(0));
        _runner.Plan(default!, default, default!, default, default)
            .ReturnsForAnyArgs(_ => ToolRunPlan.Runnable("test", ["run"], FileSystem.FromUnsanitizedFullPath("/tools"), new Dictionary<string, string>()));
        var loadout = await CreateLoadout();
        await AddRow(loadout, "xEdit", "/tools/xedit.exe", toolId: "xedit");
        var definition = Catalogue("xedit", "xEdit", _ => true) with
        {
            Arguments = ArgumentTemplate.Of("-fixed"),
            ExtraArguments = (_, exeName) => [$"--for-{exeName}"],
        };

        var tool = (ModdingTool)CatalogueProvider(definition).GetTools(loadout).Single();
        await tool.Execute(loadout, CancellationToken.None);

        await _runner.Received().RunWindowsTool(Arg.Any<GameInstallation>(), Arg.Any<AbsolutePath>(),
            Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "-fixed", "--for-xedit.exe" })),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<ToolRunVia>());
    }

    [Fact]
    public async Task ASavedRowStoredWithAutoRunsHowTheDefinitionSays()
    {
        _processRunner
            .RunAsync(Arg.Do<CliWrap.Command>(c => _commands.Add(c)), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => new CliWrap.CommandResult(0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        var loadout = await CreateLoadout();
        await AddRow(loadout, "Wrye Bash", "/tools/wrye", toolId: "wrye");

        var tool = (ModdingTool)CatalogueProvider(Catalogue("wrye", "Wrye Bash", _ => true) with { RunVia = ToolRunVia.Native })
            .GetTools(loadout).Single();
        await tool.Execute(loadout, CancellationToken.None);

        _commands.Should().ContainSingle();
        await _runner.DidNotReceiveWithAnyArgs().RunWindowsTool(default!, default, default!, default, default, default, default);
    }
}
