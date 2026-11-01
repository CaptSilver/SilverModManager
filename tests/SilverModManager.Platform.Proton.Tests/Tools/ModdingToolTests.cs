using System.Diagnostics.CodeAnalysis;
using CliWrap;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Games.Generic;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
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
    private readonly IPrefixResolver _prefixResolver = Substitute.For<IPrefixResolver>();
    private readonly FakePrefixInspector _inspector = new();
    private readonly List<Command> _commands = [];
    private int _exitCode;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddDefinedGame(Definition, (provider, definition) => new SimpleGame(provider, definition))
            .AddUniversalGameLocator(Definition.GameId, locationsFileSystem: IsolatedKnownFolders.Create);
    }

    // The reason applies to every strategy except runnableVia, standing in for a prefix where Auto has no way
    // to run a program but a forced strategy does.
    private void StubRunners(string? cannotRunBecause = null, ToolRunVia? runnableVia = null)
    {
        _toolRunner.RunWindowsTool(default!, default, default!, default, default, default, default)
            .ReturnsForAnyArgs(_ => new ToolRunResult(_exitCode));
        _toolRunner.Plan(default!, default, default!, default, default)
            .ReturnsForAnyArgs(call => cannotRunBecause is not null && call.ArgAt<ToolRunVia>(4) != runnableVia
                ? ToolRunPlan.Failed(cannotRunBecause)
                : ToolRunPlan.Runnable("test", ["run"], Exe.Parent, new Dictionary<string, string>()));
        _processRunner
            .RunAsync(Arg.Do<Command>(c => _commands.Add(c)), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => new CommandResult(_exitCode, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
    }

    private AbsolutePath Exe => FileSystem.FromUnsanitizedFullPath("/tools/xEdit/SSEEdit.exe");

    private ModdingTool Tool(ToolDefinition definition, InstalledTool? installed = null)
        => new(definition, installed ?? new InstalledTool(Exe), Definition.GameId, _toolRunner, _processRunner, new ToolArgumentResolver(), _prefixResolver, ServiceProvider.GetRequiredService<ToolOutputCapture>(), new ToolPrefixSetup(_toolRunner, _prefixResolver, _inspector));

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
    public async Task WindowsTool_ThatAutoCannotRunButTheForcedStrategyCan_Runs()
    {
        StubRunners("No Wine prefix was found.", runnableVia: ToolRunVia.Umu);
        var loadout = await CreateLoadout();

        await Tool(Editor(), new InstalledTool(Exe, null, ToolRunVia.Umu)).Execute(loadout, CancellationToken.None);

        await _toolRunner.Received(1).RunWindowsTool(
            Arg.Any<GameInstallation>(), Exe, Arg.Any<IReadOnlyList<string>>(), null, true, Arg.Any<CancellationToken>(), ToolRunVia.Umu);
    }

    [Fact]
    public async Task WindowsTool_WhoseRunnerThrowsAfterThePlan_PassesTheRunnersMessageOnUnchanged()
    {
        StubRunners();
        _toolRunner.RunWindowsTool(default!, default, default!, default, default, default, default)
            .ReturnsForAnyArgs(_ => Task.FromException<ToolRunResult>(new InvalidOperationException("No Wine prefix was found.")));
        var loadout = await CreateLoadout();

        var act = () => Tool(Editor()).Execute(loadout, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No Wine prefix was found.");
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

    private static readonly GamePath OutputTarget = new(LocationId.Game, "My Mods");

    private static ToolDefinition DynDolod() => new()
    {
        Id = new ToolId("dyndolod"),
        DisplayName = "DynDOLOD",
        AppliesTo = _ => true,
        ExecutableNames = _ => ["DynDOLODx64.exe"],
        Arguments = ArgumentTemplate.Of("-O:{OutputFolder}"),
        Output = new ToolOutput.DeclaredFolder("DynDOLOD Output", OutputTarget),
        IgnoredOutput = ["*.log"],
    };

    [Fact]
    public void TheToolReportsItsDefinitionsIgnoredOutputToTheToolManager()
    {
        var tool = Tool(DynDolod());

        ((IToolWithOutput)tool).IgnoredOutput.Should().Equal("*.log");
    }

    // Stands for the tool: finds the folder it was given in the -O: word and writes a plugin and a log into it.
    private AbsolutePath UsePrefix()
    {
        var prefix = TemporaryFileManager.CreateFolder("pfx").Path;
        _prefixResolver.Resolve(default!).ReturnsForAnyArgs(PrefixResolution.Resolved(new ResolvedPrefix(prefix, CompatibilityLayerKind.SteamProton)));
        return prefix;
    }

    private AbsolutePath UsePrefixAndWriteOutput(List<string> seenArguments)
    {
        var prefix = UsePrefix();
        StubRunners();
        _toolRunner.RunWindowsTool(default!, default, default!, default, default, default, default)
            .ReturnsForAnyArgs(call =>
            {
                var args = call.ArgAt<IReadOnlyList<string>>(2);
                seenArguments.AddRange(args);
                var folder = FileSystem.FromUnsanitizedFullPath(args.Single(a => a.StartsWith("-O:"))[3..]);
                File.WriteAllText((folder / "DynDOLOD.esp").GetFullPath(), "lod plugin");
                File.WriteAllText((folder / "DynDOLOD.log").GetFullPath(), "noise");
                return new ToolRunResult(_exitCode);
            });
        return prefix;
    }

    private IEnumerable<LoadoutItemGroup.ReadOnly> GroupsNamed(Loadout.ReadOnly loadout, string name) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Where(g => g.AsLoadoutItem().Name == name);

    [Fact]
    public async Task DeclaredFolderTool_GetsARunFolderInThePrefixsCDriveAndItsOutputIsCaptured()
    {
        var seen = new List<string>();
        var prefix = UsePrefixAndWriteOutput(seen);
        var loadout = await CreateLoadout();

        await Tool(DynDolod()).Execute(loadout, CancellationToken.None);

        var runFolder = seen.Single().Substring("-O:".Length);
        var expectedRoot = (prefix / "drive_c/SilverModManager/tool-output/dyndolod").GetFullPath();
        runFolder.Should().StartWith(expectedRoot + "/");
        runFolder[(expectedRoot.Length + 1)..].Should().MatchRegex(@"^\d{8}-\d{6}$");
        WindowsPathTranslator.Translate("-O:" + runFolder, prefix.GetFullPath())
            .Should().StartWith(@"-O:C:\SilverModManager\tool-output\dyndolod\");

        var group = GroupsNamed(loadout, "DynDOLOD Output").Should().ContainSingle().Subject;
        group.Children.OfTypeLoadoutItemWithTargetPath().Select(c => (GamePath)c.TargetPath)
            .Should().ContainSingle("the log is ignored").Which.Should().Be(new GamePath(LocationId.Game, "My Mods/DynDOLOD.esp"));
        FileSystem.FromUnsanitizedFullPath(runFolder).DirectoryExists().Should().BeFalse("the folder is deleted after capture");
    }

    [Fact]
    public async Task DeclaredFolderTool_ThatFails_KeepsItsRunFolderAndNamesItInTheMessage()
    {
        var seen = new List<string>();
        UsePrefixAndWriteOutput(seen);
        _exitCode = 3;
        var loadout = await CreateLoadout();

        var act = () => Tool(DynDolod()).Execute(loadout, CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        var runFolder = seen.Single().Substring("-O:".Length);
        failure.Message.Should().Contain("DynDOLOD").And.Contain("3").And.Contain(runFolder);
        FileSystem.FromUnsanitizedFullPath(runFolder).DirectoryExists().Should().BeTrue();
        GroupsNamed(loadout, "DynDOLOD Output").Should().BeEmpty();
    }

    [Fact]
    public async Task DeclaredFolderTool_WithoutACatalogueId_IsRefusedBeforeAnythingStarts()
    {
        UsePrefixAndWriteOutput([]);
        var loadout = await CreateLoadout();

        var act = () => Tool(DynDolod() with { Id = null }).Execute(loadout, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DynDOLOD*catalogue*");
        await _toolRunner.DidNotReceiveWithAnyArgs().RunWindowsTool(default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task DeclaredFolderTool_RunNatively_IsRefusedBecauseTheRunFolderLivesInAPrefix()
    {
        UsePrefixAndWriteOutput([]);
        var loadout = await CreateLoadout();

        var act = () => Tool(DynDolod(), new InstalledTool(Exe, null, ToolRunVia.Native)).Execute(loadout, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DynDOLOD*prefix*");
        _commands.Should().BeEmpty();
    }

    [Fact]
    public async Task DeclaredFolderTool_WithoutAPrefix_IsRefusedWithTheResolversReason()
    {
        StubRunners();
        _prefixResolver.Resolve(default!).ReturnsForAnyArgs(PrefixResolution.Failed("Two prefixes found."));
        var loadout = await CreateLoadout();

        var act = () => Tool(DynDolod()).Execute(loadout, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DynDOLOD*Two prefixes found.*");
        await _toolRunner.DidNotReceiveWithAnyArgs().RunWindowsTool(default!, default, default!, default, default, default, default);
    }

    private IEnumerable<AbsolutePath> RunFoldersIn(AbsolutePath prefix)
    {
        var root = prefix / "drive_c/SilverModManager/tool-output/dyndolod";
        return root.DirectoryExists() ? Directory.EnumerateDirectories(root.GetFullPath()).Select(d => FileSystem.FromUnsanitizedFullPath(d)) : [];
    }

    [Fact]
    public async Task DeclaredFolderTool_ThatTheRunnerRefuses_LeavesNoRunFolderBehind()
    {
        var prefix = UsePrefix();
        StubRunners("No Wine prefix was found.");
        var loadout = await CreateLoadout();

        var act = () => Tool(DynDolod()).Execute(loadout, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No Wine prefix was found.*");
        RunFoldersIn(prefix).Should().BeEmpty();
    }

    [Fact]
    public async Task DeclaredFolderTool_WhoseRunnerThrowsAfterTheStart_KeepsTheFolderAndNamesItInTheMessage()
    {
        var seen = new List<string>();
        var prefix = UsePrefix();
        StubRunners();
        _toolRunner.RunWindowsTool(default!, default, default!, default, default, default, default)
            .ReturnsForAnyArgs<Task<ToolRunResult>>(call =>
            {
                seen.AddRange(call.ArgAt<IReadOnlyList<string>>(2));
                throw new IOException("wine went away");
            });
        var loadout = await CreateLoadout();

        var act = () => Tool(DynDolod()).Execute(loadout, CancellationToken.None);

        var failure = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        var runFolder = seen.Single().Substring("-O:".Length);
        failure.InnerException.Should().BeOfType<IOException>();
        failure.Message.Should().Contain("wine went away").And.Contain(runFolder);
        RunFoldersIn(prefix).Should().ContainSingle();
    }

    [Fact]
    public async Task ARunRecordsItsExitCode()
    {
        StubRunners();
        _exitCode = 3;
        var loadout = await CreateLoadout();
        var tool = Tool(Editor());
        tool.LastRun.Should().BeNull("nothing has run yet");

        await tool.Execute(loadout, CancellationToken.None);

        tool.LastRun.Should().Be(new ToolRunSummary(3, CapturedFiles: 0));
    }

    [Fact]
    public async Task ADeclaredFolderRunRecordsHowManyFilesItCaptured()
    {
        UsePrefixAndWriteOutput([]);
        var loadout = await CreateLoadout();
        var tool = Tool(DynDolod());

        await tool.Execute(loadout, CancellationToken.None);

        tool.LastRun.Should().Be(new ToolRunSummary(0, CapturedFiles: 1));
    }

    [Fact]
    public void TheToolReportsItsCatalogueIdAndPrefixRequirements()
    {
        var definition = DynDolod() with { Prefix = new ToolPrefixRequirements { Components = [Vc] } };
        var tool = Tool(definition);

        tool.CatalogueId.Should().Be(new ToolId("dyndolod"));
        tool.Prefix.Components.Should().Equal(Vc);
        Tool(Editor()).CatalogueId.Should().BeNull();
    }

    [Fact]
    public async Task MissingPrerequisites_NamesTheComponentsTheInspectorSaysAreMissing()
    {
        UsePrefix();
        _inspector.State = id => id == Vc ? PrefixComponentState.Missing : PrefixComponentState.Installed;
        var loadout = await CreateLoadout();

        Tool(XEdit(new ToolPrefixRequirements { Components = [Vc] })).MissingPrerequisites(loadout.InstallationInstance)
            .Should().Equal(PrefixComponents.VisualCppRuntime.DisplayName);
    }

    [Fact]
    public async Task MissingPrerequisiteIds_GivesTheIdsThePrefixSetupInstalls()
    {
        UsePrefix();
        _inspector.State = id => id == Vc ? PrefixComponentState.Missing : PrefixComponentState.Installed;
        var loadout = await CreateLoadout();

        Tool(XEdit(new ToolPrefixRequirements { Components = [Vc] })).MissingPrerequisiteIds(loadout.InstallationInstance)
            .Should().Equal(Vc);
    }

    [Fact]
    public async Task MissingPrerequisites_IsEmptyWhenNothingIsRequiredOrTheComponentIsThere()
    {
        UsePrefix();
        var loadout = await CreateLoadout();

        Tool(Editor()).MissingPrerequisites(loadout.InstallationInstance).Should().BeEmpty();
        Tool(XEdit(new ToolPrefixRequirements { Components = [Vc] })).MissingPrerequisites(loadout.InstallationInstance).Should().BeEmpty();
    }

    private static ToolDefinition WithQuickAutoClean(ToolDefinition definition) => definition with
    {
        Arguments = ArgumentTemplate.Of("-D:{DataFolder}"),
        QuickAutoClean = new ToolVariant("Quick Auto Clean", ArgumentTemplate.Of("-quickautoclean", "{Plugin}"), ExitCodePolicy.ZeroIsSuccess),
    };

    [Fact]
    public void QuickAutoClean_IsOnlyOfferedWhenTheDefinitionHasTheVariant()
    {
        Tool(Editor()).ForQuickAutoClean("Update.esm").Should().BeNull();
        Tool(WithQuickAutoClean(Editor())).CanQuickAutoClean.Should().BeTrue();
        Tool(Editor()).CanQuickAutoClean.Should().BeFalse();
    }

    [Fact]
    public async Task QuickAutoClean_RunsTheVariantsArgumentsWithThePickedPlugin()
    {
        StubRunners();
        var loadout = await CreateLoadout();
        var clean = Tool(WithQuickAutoClean(Editor())).ForQuickAutoClean("Update.esm")!;

        clean.Name.Should().Be("xEdit: Quick Auto Clean");
        await clean.Execute(loadout, CancellationToken.None);

        await _toolRunner.Received(1).RunWindowsTool(
            Arg.Any<GameInstallation>(), Exe,
            Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "-quickautoclean", "Update.esm" })),
            null, true, Arg.Any<CancellationToken>(), ToolRunVia.Auto);
    }

    [Fact]
    public async Task QuickAutoClean_ThatExitsNonZero_IsAFailedRun()
    {
        StubRunners();
        _exitCode = 2;
        var loadout = await CreateLoadout();
        var clean = Tool(WithQuickAutoClean(Editor())).ForQuickAutoClean("Update.esm")!;

        var act = () => clean.Execute(loadout, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("exited with code 2");
    }

    private static readonly PrefixComponentId Vc = PrefixComponents.VisualCppRuntime.Id;

    private static ToolDefinition XEdit(ToolPrefixRequirements prefix) => Editor() with { Prefix = prefix };

    private static ToolPrefixRequirements WinXpFor(params string[] exeNames) => new()
    {
        RegistryDefaults = [.. exeNames.Select(name => new PrefixRegistryDefault(name, "Version", "winxp"))],
    };

    private static bool IsRegAdd(PrefixCommand command, string exeName, string value, string data) =>
        command.IsVerb
        && command.Program == "reg"
        && command.Args.SequenceEqual(new[] { "add", $@"HKCU\Software\Wine\AppDefaults\{exeName}", "/v", value, "/d", data, "/f" });

    // A reg add that succeeds, and puts the value where the inspector reads it back.
    private void RegAddWritesToThePrefix()
    {
        _toolRunner.RunInPrefix(default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                var args = call.ArgAt<PrefixCommand>(1).Args;
                var exe = args[1][(args[1].LastIndexOf('\\') + 1)..];
                _inspector.AppDefaults[(exe, args[3])] = args[5];
                return new PrefixRunResult(0, "", "");
            });
    }

    [Fact]
    public async Task RegistryDefaults_AreWrittenWithRegAddBeforeTheToolStarts()
    {
        StubRunners();
        UsePrefix();
        RegAddWritesToThePrefix();
        var loadout = await CreateLoadout();

        await Tool(XEdit(WinXpFor("SSEEdit.exe", "SSEEdit64.exe"))).Execute(loadout, CancellationToken.None);

        Received.InOrder(() =>
        {
            _toolRunner.RunInPrefix(loadout.InstallationInstance, Arg.Is<PrefixCommand>(c => IsRegAdd(c, "SSEEdit.exe", "Version", "winxp")), Arg.Any<CancellationToken>());
            _toolRunner.RunInPrefix(loadout.InstallationInstance, Arg.Is<PrefixCommand>(c => IsRegAdd(c, "SSEEdit64.exe", "Version", "winxp")), Arg.Any<CancellationToken>());
            _toolRunner.RunWindowsTool(loadout.InstallationInstance, Exe, Arg.Any<IReadOnlyList<string>>(), null, true, Arg.Any<CancellationToken>(), ToolRunVia.Auto);
        });
    }

    [Fact]
    public async Task RegistryDefault_IsWrittenOnceAcrossRuns()
    {
        StubRunners();
        UsePrefix();
        RegAddWritesToThePrefix();
        var loadout = await CreateLoadout();
        var tool = Tool(XEdit(WinXpFor("SSEEdit.exe")));

        await tool.Execute(loadout, CancellationToken.None);
        await tool.Execute(loadout, CancellationToken.None);

        await _toolRunner.ReceivedWithAnyArgs(1).RunInPrefix(default!, default!, default);
        await _toolRunner.ReceivedWithAnyArgs(2).RunWindowsTool(default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task RegistryDefault_ThatTheUserRegAlreadyHolds_IsNotWritten()
    {
        StubRunners();
        UsePrefix();
        RegAddWritesToThePrefix();
        _inspector.AppDefaults[("SSEEdit.exe", "Version")] = "winxp";
        var loadout = await CreateLoadout();

        await Tool(XEdit(WinXpFor("SSEEdit.exe", "SSEEdit64.exe"))).Execute(loadout, CancellationToken.None);

        await _toolRunner.Received(1).RunInPrefix(Arg.Any<GameInstallation>(), Arg.Is<PrefixCommand>(c => IsRegAdd(c, "SSEEdit64.exe", "Version", "winxp")), Arg.Any<CancellationToken>());
        await _toolRunner.ReceivedWithAnyArgs(1).RunInPrefix(default!, default!, default);
    }

    [Fact]
    public async Task RegistryDefault_WithAnotherValueInThePrefix_IsWrittenOver()
    {
        StubRunners();
        UsePrefix();
        RegAddWritesToThePrefix();
        _inspector.AppDefaults[("SSEEdit.exe", "Version")] = "win10";
        var loadout = await CreateLoadout();

        await Tool(XEdit(WinXpFor("SSEEdit.exe"))).Execute(loadout, CancellationToken.None);

        await _toolRunner.Received(1).RunInPrefix(Arg.Any<GameInstallation>(), Arg.Is<PrefixCommand>(c => IsRegAdd(c, "SSEEdit.exe", "Version", "winxp")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegistryDefault_ThatFailsToWrite_RefusesTheRunNamingTheToolAndExe()
    {
        StubRunners();
        UsePrefix();
        _toolRunner.RunInPrefix(default!, default!, default)
            .ReturnsForAnyArgs(new PrefixRunResult(1, "", "access denied"));
        var loadout = await CreateLoadout();

        var act = () => Tool(XEdit(WinXpFor("SSEEdit.exe"))).Execute(loadout, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("xEdit").And.Contain("SSEEdit.exe").And.Contain("access denied");
        await _toolRunner.DidNotReceiveWithAnyArgs().RunWindowsTool(default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task MissingComponent_RefusesTheRunNamingItAndNeverStartsTheTool()
    {
        StubRunners();
        UsePrefix();
        _inspector.State = id => id == Vc ? PrefixComponentState.Missing : PrefixComponentState.Installed;
        var loadout = await CreateLoadout();

        var act = () => Tool(XEdit(new ToolPrefixRequirements { Components = [Vc] })).Execute(loadout, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("xEdit").And.Contain(PrefixComponents.VisualCppRuntime.DisplayName);
        await _toolRunner.DidNotReceiveWithAnyArgs().RunWindowsTool(default!, default, default!, default, default, default, default);
        await _toolRunner.DidNotReceiveWithAnyArgs().RunInPrefix(default!, default!, default);
    }

    [Fact]
    public async Task MissingComponent_IsCheckedBeforeAnyRegistryDefaultIsWritten()
    {
        StubRunners();
        UsePrefix();
        _inspector.State = _ => PrefixComponentState.Missing;
        var loadout = await CreateLoadout();
        var prefix = WinXpFor("SSEEdit.exe") with { Components = [Vc] };

        var act = () => Tool(XEdit(prefix)).Execute(loadout, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _toolRunner.DidNotReceiveWithAnyArgs().RunInPrefix(default!, default!, default);
    }

    [Theory]
    [InlineData(PrefixComponentState.Installed)]
    [InlineData(PrefixComponentState.Unknown)]
    public async Task Component_ThatIsNotMissing_LetsTheToolRun(PrefixComponentState state)
    {
        StubRunners();
        UsePrefix();
        _inspector.State = _ => state;
        var loadout = await CreateLoadout();

        await Tool(XEdit(new ToolPrefixRequirements { Components = [Vc] })).Execute(loadout, CancellationToken.None);

        await _toolRunner.ReceivedWithAnyArgs(1).RunWindowsTool(default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task ToolWithoutPrefixRequirements_NeitherInspectsNorWritesThePrefix()
    {
        StubRunners();
        var loadout = await CreateLoadout();

        await Tool(Editor()).Execute(loadout, CancellationToken.None);

        _inspector.Calls.Should().BeEmpty();
        _prefixResolver.ReceivedCalls().Should().BeEmpty();
        await _toolRunner.DidNotReceiveWithAnyArgs().RunInPrefix(default!, default!, default);
    }

    [Fact]
    public async Task NativeTool_DoesNotTouchThePrefixItsDefinitionDescribes()
    {
        StubRunners();
        _inspector.State = _ => PrefixComponentState.Missing;
        var loadout = await CreateLoadout();
        var definition = XEdit(WinXpFor("SSEEdit.exe") with { Components = [Vc] });

        await Tool(definition, new InstalledTool(Exe, null, ToolRunVia.Native)).Execute(loadout, CancellationToken.None);

        _inspector.Calls.Should().BeEmpty();
        await _toolRunner.DidNotReceiveWithAnyArgs().RunInPrefix(default!, default!, default);
    }

    [Fact]
    public async Task PrefixRequirements_WithNoResolvedPrefix_RefuseWithTheResolversReason()
    {
        StubRunners();
        _prefixResolver.Resolve(default!).ReturnsForAnyArgs(PrefixResolution.Failed("No Wine prefix was found."));
        var loadout = await CreateLoadout();

        var act = () => Tool(XEdit(WinXpFor("SSEEdit.exe"))).Execute(loadout, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("xEdit").And.Contain("No Wine prefix was found.");
        await _toolRunner.DidNotReceiveWithAnyArgs().RunWindowsTool(default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task PrefixSetupThatRefuses_LeavesNoRunFolderBehind()
    {
        var prefix = UsePrefixAndWriteOutput([]);
        _inspector.State = _ => PrefixComponentState.Missing;
        var loadout = await CreateLoadout();

        var act = () => Tool(DynDolod() with { Prefix = new ToolPrefixRequirements { Components = [Vc] } }).Execute(loadout, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        RunFoldersIn(prefix).Should().BeEmpty();
    }
}
