using System.Diagnostics.CodeAnalysis;
using CliWrap;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Platform.Proton.Tests.Tools;

/// <summary>
/// One runner starts every tool the user runs: it fills in the arguments from the loadout, starts the program in
/// the prefix or natively, and decides from the exit code whether the run failed.
/// </summary>
public class ModdingToolTests(ITestOutputHelper helper)
    : AIsolatedDefinedGameTest<ModdingToolTests>(helper, Definition.GameId)
{
    private static readonly SimpleGameDefinition Definition = new()
    {
        GameId = GameId.From("Tests.ModdingTool"),
        DisplayName = "Modding Tool Game",
        NexusModsGameId = Optional<Sdk.NexusModsApi.NexusModsGameId>.None,
        Stores = new StoreIdentifiers(GameId.From("Tests.ModdingTool")),
        Executable = "bin/game.exe",
        ModRoot = new ModLocation(ModLocationRoot.GameFolder, "My Mods"),
        Install = new SimpleInstallRules(),
    };

    private readonly IToolRunner _toolRunner = Substitute.For<IToolRunner>();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly List<Command> _commands = [];
    private int _exitCode;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddDefinedGame(Definition, (provider, definition) => new SimpleGame(provider, definition))
            .AddUniversalGameLocator(Definition.GameId, locationsFileSystem: IsolatedKnownFolders.Create);
    }

    private void StubRunners(string? cannotRunBecause = null)
    {
        _toolRunner.RunWindowsTool(default!, default, default!, default, default, default, default)
            .ReturnsForAnyArgs(_ => new ToolRunResult(_exitCode));
        _toolRunner.CanRun(Arg.Any<GameInstallation>(), out Arg.Any<string?>()).Returns(call =>
        {
            call[1] = cannotRunBecause;
            return cannotRunBecause is null;
        });
        _processRunner
            .RunAsync(Arg.Do<Command>(c => _commands.Add(c)), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => new CommandResult(_exitCode, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
    }

    private AbsolutePath Exe => FileSystem.FromUnsanitizedFullPath("/tools/xEdit/SSEEdit.exe");

    private ModdingTool Tool(ToolDefinition definition, InstalledTool? installed = null)
        => new(definition, installed ?? new InstalledTool(Exe), Definition.GameId, _toolRunner, _processRunner, new ToolArgumentResolver());

    private static ToolDefinition Editor(ToolRunVia via = ToolRunVia.Auto) => ToolDefinition.AdHoc(Definition.GameId, "xEdit", "SSEEdit.exe", via);

    private AbsolutePath GameFolder => GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));

    [Theory]
    [InlineData(ToolRunVia.Auto)]
    [InlineData(ToolRunVia.Umu)]
    public async Task WindowsTool_RunsThroughTheToolRunnerWithItsSplitArgumentsAndForcedStrategy(ToolRunVia via)
    {
        StubRunners();
        var loadout = await CreateLoadout();

        await Tool(Editor(), new InstalledTool(Exe, "-SSE \"-o:C:\\out dir\"", via)).Execute(loadout, CancellationToken.None);

        await _toolRunner.Received(1).RunWindowsTool(
            loadout.InstallationInstance,
            Exe,
            Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "-SSE", "-o:C:\\out dir" })),
            null,
            true,
            Arg.Any<CancellationToken>(),
            via);
        _commands.Should().BeEmpty();
    }

    [Fact]
    public async Task TheDefinitionsRunVia_IsUsedWhenTheUserSetsNone()
    {
        StubRunners();
        var loadout = await CreateLoadout();

        await Tool(Editor(ToolRunVia.Umu)).Execute(loadout, CancellationToken.None);

        await _toolRunner.Received(1).RunWindowsTool(
            Arg.Any<GameInstallation>(), Exe, Arg.Any<IReadOnlyList<string>>(), null, true, Arg.Any<CancellationToken>(), ToolRunVia.Umu);
    }

    [Fact]
    public async Task Arguments_AreFilledInFromTheLoadoutAsHostPaths()
    {
        StubRunners();
        var loadout = await CreateLoadout();
        var dataFolder = GameFolder.Combine("My Mods").GetFullPath();

        await Tool(Editor(), new InstalledTool(Exe, "-D:{DataFolder} \"-x y\" {Exe}")).Execute(loadout, CancellationToken.None);

        await _toolRunner.Received(1).RunWindowsTool(
            Arg.Any<GameInstallation>(),
            Exe,
            Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { $"-D:{dataFolder}", "-x y", Exe.GetFullPath() })),
            null,
            true,
            Arg.Any<CancellationToken>(),
            ToolRunVia.Auto);
    }

    [Fact]
    public async Task CatalogueTool_UsesItsTemplateThenTheGameSpecificWords()
    {
        StubRunners();
        var loadout = await CreateLoadout();
        var definition = Editor() with
        {
            Arguments = ArgumentTemplate.Of("-D:{DataFolder}"),
            ExtraArguments = (_, exeName) => [$"--for-{exeName}"],
        };

        await Tool(definition).Execute(loadout, CancellationToken.None);

        await _toolRunner.Received(1).RunWindowsTool(
            Arg.Any<GameInstallation>(),
            Exe,
            Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { $"-D:{GameFolder.Combine("My Mods").GetFullPath()}", "--for-SSEEdit.exe" })),
            null,
            true,
            Arg.Any<CancellationToken>(),
            ToolRunVia.Auto);
    }

    [Fact]
    public async Task UnknownVariable_IsRefusedBeforeAnythingStarts()
    {
        StubRunners();
        var loadout = await CreateLoadout();

        var act = () => Tool(Editor(), new InstalledTool(Exe, "{Nonsense}")).Execute(loadout, CancellationToken.None);

        await act.Should().ThrowAsync<UnknownToolVariableException>();
        await _toolRunner.DidNotReceiveWithAnyArgs().RunWindowsTool(default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task NativeTool_StartsTheProgramDirectlyThroughTheProcessRunner()
    {
        StubRunners();
        var loadout = await CreateLoadout();

        await Tool(Editor(), new InstalledTool(Exe, "--flag \"two words\"", ToolRunVia.Native)).Execute(loadout, CancellationToken.None);

        var command = _commands.Should().ContainSingle().Subject;
        command.TargetFilePath.Should().Be(Exe.ToString());
        command.Arguments.Should().Be("--flag \"two words\"");
        command.WorkingDirPath.Should().Be(Exe.Parent.ToString());
        await _toolRunner.DidNotReceiveWithAnyArgs().RunWindowsTool(default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task WindowsTool_ThatCannotRun_IsRefusedWithTheRunnersReasonAndNeverStarted()
    {
        StubRunners("No Wine prefix was found.");
        var loadout = await CreateLoadout();

        var act = () => Tool(Editor()).Execute(loadout, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("xEdit").And.Contain("No Wine prefix was found.");
        await _toolRunner.DidNotReceiveWithAnyArgs().RunWindowsTool(default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task NativeTool_NeedsNoPrefix()
    {
        StubRunners("No Wine prefix was found.");
        var loadout = await CreateLoadout();

        await Tool(Editor(), new InstalledTool(Exe, null, ToolRunVia.Native)).Execute(loadout, CancellationToken.None);

        _commands.Should().ContainSingle();
    }

    [Theory]
    [InlineData(ToolRunVia.Auto)]
    [InlineData(ToolRunVia.Native)]
    public async Task ZeroIsSuccess_NonZeroExitFailsTheRunNamingTheToolAndCode(ToolRunVia via)
    {
        StubRunners();
        _exitCode = 3;
        var loadout = await CreateLoadout();
        var definition = Editor() with { ExitCodes = ExitCodePolicy.ZeroIsSuccess };

        var act = () => Tool(definition, new InstalledTool(Exe, null, via)).Execute(loadout, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("xEdit").And.Contain("3");
    }

    [Theory]
    [InlineData(ToolRunVia.Auto)]
    [InlineData(ToolRunVia.Native)]
    public async Task ZeroIsSuccess_ZeroExitIsFine(ToolRunVia via)
    {
        StubRunners();
        var loadout = await CreateLoadout();
        var definition = Editor() with { ExitCodes = ExitCodePolicy.ZeroIsSuccess };

        var act = () => Tool(definition, new InstalledTool(Exe, null, via)).Execute(loadout, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(ToolRunVia.Auto)]
    [InlineData(ToolRunVia.Native)]
    public async Task AnyExitIsFine_NonZeroExitIsNotAnError(ToolRunVia via)
    {
        StubRunners();
        _exitCode = 3;
        var loadout = await CreateLoadout();

        var act = () => Tool(Editor(), new InstalledTool(Exe, null, via)).Execute(loadout, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Tool_IsListedForTheGameItWasCreatedForUnderTheDefinitionsName()
    {
        var tool = Tool(Editor());

        tool.GameIds.Should().Equal(Definition.GameId);
        tool.Name.Should().Be("xEdit");
        tool.Exe.Should().Be(Exe);
    }

    [Fact]
    public void AdHocDefinition_HasNoCatalogueIdAndAppliesToItsGameOnly()
    {
        var definition = Editor();

        definition.Id.Should().BeNull();
        definition.AppliesTo(Game).Should().BeTrue();
        definition.ExitCodes.Should().Be(ExitCodePolicy.AnyExitIsFine);
        definition.ExecutableNames(Game).Should().Equal("SSEEdit.exe");
        ToolDefinition.AdHoc(GameId.From("Tests.Other"), "x", "x.exe").AppliesTo(Game).Should().BeFalse();
    }
}
