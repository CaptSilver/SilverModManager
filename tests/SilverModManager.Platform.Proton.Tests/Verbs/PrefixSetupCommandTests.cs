using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.PrefixSetup;
using SilverModManager.Platform.Proton.Verbs;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Platform.Proton.Tests.Verbs;

/// <summary>
/// The <c>prefix setup</c> verb against a recording runner: what it prints, and which requests it lets through.
/// </summary>
public class PrefixSetupCommandTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<PrefixSetupCommandTests>(helper)
{
    private const int TimeoutMs = 60000;

    private static readonly AbsolutePath PrefixPath = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/games/compatdata/42/pfx");
    private static readonly AbsolutePath BackupPath = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/data/silvermod/prefix-backups/0123456789abcdef/20261026T123000Z");
    private const string Url = "https://example.invalid/vc/VC_redist.x64.exe";
    private const string Hash = "ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34";

    private sealed class RecordingRenderer : IRenderer
    {
        public List<string> Lines { get; } = [];

        public string Text => string.Join('\n', Lines);

        public ValueTask RenderAsync(IRenderable renderable)
        {
            switch (renderable)
            {
                case Text text:
                    Lines.AddRange(text.Template.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                    break;
                case Table table:
                    Lines.Add(string.Join('\t', table.Columns.OfType<Text>().Select(c => c.Template)));
                    Lines.AddRange(table.Rows.Select(r => string.Join('\t', r.OfType<Text>().Select(c => c.Template))));
                    break;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRunner : IPrefixSetupRunner
    {
        public List<PrefixSetupRequest> Requests { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public PrefixSetupStepStatus StepStatus { get; set; } = PrefixSetupStepStatus.Installed;

        public Task<PrefixSetupResult> Run(GameInstallation installation, PrefixSetupRequest request, CancellationToken cancellationToken, Func<PrefixSetupStepResult, ValueTask>? stepFinished = null)
        {
            Requests.Add(request);
            Tokens.Add(cancellationToken);
            var entries = request.Components.Select(id => new PrefixSetupPlanEntry(
                id,
                id.Value == "vcrun2022" ? "Visual C++ runtime" : id.Value,
                new PrefixComponentStatus(id, PrefixComponentState.Missing, "only Wine placeholders"),
                [new PrefixDownload(Url, Hash, "VC_redist.x64.exe")],
                ["run VC_redist.x64.exe /install /quiet"],
                WillInstall: true)).ToList();
            var plan = new PrefixSetupPlan(new ResolvedPrefix(PrefixPath, CompatibilityLayerKind.SteamProton, ProtonAppId: 42), entries, BackupPath, "Steam prefix shared note.");
            if (!request.Write) return Task.FromResult(new PrefixSetupResult(plan, Written: false, []));

            var steps = entries.Select(e => new PrefixSetupStepResult(e.Id, e.Name, StepStatus, StepStatus == PrefixSetupStepStatus.Failed ? "installer exited with 1" : null)).ToList();
            return RunSteps(plan, steps, stepFinished);
        }

        private static async Task<PrefixSetupResult> RunSteps(PrefixSetupPlan plan, List<PrefixSetupStepResult> steps, Func<PrefixSetupStepResult, ValueTask>? stepFinished)
        {
            foreach (var step in steps)
            {
                if (stepFinished is not null) await stepFinished(step);
            }

            return new PrefixSetupResult(plan, Written: true, steps);
        }
    }

    private readonly RecordingRunner _runner = new();

    private PrefixSetupCommand Command() => new(
        new LoadoutResolver(Connection, GameRegistry, Substitute.For<IEnvironmentReader>()),
        _runner);

    private async Task<string> LoadoutName()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        return (await CreateLoadout()).Name;
    }

    private async Task<(int ExitCode, RecordingRenderer Output)> Run(bool write = false, string? prefix = null, string? loadout = null)
    {
        var renderer = new RecordingRenderer();
        var exitCode = await Command().RunAsync(renderer, loadout ?? await LoadoutName(), game: null, write, prefix);
        return (exitCode, renderer);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task DefaultPrintsThePlanAndAsksForADryRun()
    {
        var (exitCode, output) = await Run();

        exitCode.Should().Be(0, output.Text);
        _runner.Requests.Should().ContainSingle().Which.Should().Match<PrefixSetupRequest>(r => !r.Write && r.ConfirmedPrefixPath == null);
        _runner.Requests[0].Components.Select(c => c.Value).Should().Equal("vcrun2022", "d3dcompiler_47");
        output.Text.Should().Contain(PrefixPath.ToString());
        output.Text.Should().Contain("Visual C++ runtime");
        output.Text.Should().Contain(Url).And.Contain(Hash);
        output.Text.Should().Contain("run VC_redist.x64.exe /install /quiet");
        output.Text.Should().Contain(BackupPath.ToString());
        output.Text.Should().Contain("Steam prefix shared note.");
        output.Text.Should().Contain($"--write --prefix '{PrefixPath}'", "the dry run says exactly what to type to apply it");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task WriteWithoutPrefixIsRefusedNamingTheFlag()
    {
        var act = () => Run(write: true);

        (await act.Should().ThrowAsync<PrefixSetupRefusedException>()).Which.Message.Should().Contain("--prefix");
        _runner.Requests.Should().BeEmpty("nothing is planned, let alone written, without the consent");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task PrefixWithoutWriteIsRefused()
    {
        var act = () => Run(prefix: PrefixPath.ToString());

        (await act.Should().ThrowAsync<PrefixSetupRefusedException>()).Which.Message.Should().Contain("--write");
        _runner.Requests.Should().BeEmpty();
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task WriteWithAnotherPrefixIsRefusedBeforeAnyWriteRequest()
    {
        var act = () => Run(write: true, prefix: "/games/compatdata/99/pfx");

        var message = (await act.Should().ThrowAsync<PrefixSetupRefusedException>()).Which.Message;
        message.Should().Contain("/games/compatdata/99/pfx").And.Contain(PrefixPath.ToString());
        _runner.Requests.Should().OnlyContain(r => !r.Write);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task WriteWithTheResolvedPrefixPassesTheConfirmation()
    {
        var (exitCode, output) = await Run(write: true, prefix: PrefixPath.ToString());

        exitCode.Should().Be(0, output.Text);
        var write = _runner.Requests.Last();
        write.Write.Should().BeTrue();
        write.ConfirmedPrefixPath.Should().Be(PrefixPath.ToString());
        write.Components.Select(c => c.Value).Should().Equal("vcrun2022", "d3dcompiler_47");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task TheCommandsTokenReachesBothRuns()
    {
        using var cts = new CancellationTokenSource();
        var name = await LoadoutName();

        await Command().RunAsync(new RecordingRenderer(), name, game: null, write: true, PrefixPath.ToString(), cts.Token);

        _runner.Tokens.Should().HaveCount(2).And.OnlyContain(t => t == cts.Token, "cancelling the command stops the setup between steps");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task WritePrintsEachStepAsItFinishesAndAFinalTable()
    {
        var (_, output) = await Run(write: true, prefix: PrefixPath.ToString());

        output.Lines.Should().Contain(l => l.Contains("Visual C++ runtime") && l.Contains("Installed") && !l.Contains('\t'), "progress line per step");
        output.Lines.Should().Contain(l => l.StartsWith("Component") && l.Contains("Result"));
        output.Lines.Should().Contain(l => l.StartsWith("Visual C++ runtime\t") && l.Contains("Installed"));
        output.Lines.Should().Contain(l => l.StartsWith("d3dcompiler_47\t") && l.Contains("Installed"));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AFailedStepThrowsAfterTheTableIsPrinted()
    {
        _runner.StepStatus = PrefixSetupStepStatus.Failed;
        var renderer = new RecordingRenderer();
        var name = await LoadoutName();

        var act = () => Command().RunAsync(renderer, name, game: null, write: true, PrefixPath.ToString());

        (await act.Should().ThrowAsync<PrefixSetupFailedException>()).Which.Message.Should().Contain("installer exited with 1");
        renderer.Lines.Should().Contain(l => l.StartsWith("Visual C++ runtime\t") && l.Contains("Failed"));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task UnknownLoadoutPrintsTheResolverErrorAndRunsNothing()
    {
        var (exitCode, output) = await Run(loadout: "No such loadout");

        exitCode.Should().Be(LaunchSession.FailureExitCode);
        output.Text.Should().Contain("No such loadout");
        _runner.Requests.Should().BeEmpty();
    }
}
