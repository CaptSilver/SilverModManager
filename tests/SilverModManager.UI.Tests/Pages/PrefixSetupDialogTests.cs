using System.Reactive.Linq;
using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Pages.Diagnostics.PrefixSetup;
using SilverModManager.Platform.Proton.PrefixSetup;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests.Pages;

public class PrefixSetupDialogTests(IServiceProvider provider) : AUiTest(provider)
{
    private const string PrefixPath = "/nonexistent/steam/compatdata/1091500/pfx";
    private const string BackupPath = "/nonexistent/data/prefix-backups/0123456789abcdef/20260101T000000Z";
    private static readonly PrefixComponentId Vcrun = new("vcrun2022");
    private static readonly PrefixComponentId D3d = new("d3dcompiler_47");

    /// <summary>A runner that records every request and never touches a prefix.</summary>
    private sealed class FakeRunner : IPrefixSetupRunner
    {
        public List<PrefixSetupRequest> Requests { get; } = [];
        public CancellationToken LastToken { get; private set; }
        public Exception? DryRunRefusal { get; set; }
        public Func<PrefixSetupRequest, Func<PrefixSetupStepResult, ValueTask>?, Task<PrefixSetupResult>>? OnWrite { get; set; }

        public async Task<PrefixSetupResult> Run(GameInstallation installation, PrefixSetupRequest request, CancellationToken cancellationToken, Func<PrefixSetupStepResult, ValueTask>? stepFinished = null)
        {
            LastToken = cancellationToken;
            lock (Requests) Requests.Add(request);
            if (!request.Write && DryRunRefusal is not null) throw DryRunRefusal;
            if (!request.Write) return new PrefixSetupResult(MakePlan(request.Components), Written: false, []);
            if (OnWrite is null) throw new InvalidOperationException("The test gave the fake runner no write behaviour.");
            return await OnWrite(request, stepFinished);
        }
    }

    private static PrefixSetupPlan MakePlan(IReadOnlyList<PrefixComponentId> components)
    {
        var entries = components.Select(id => new PrefixSetupPlanEntry(
            id,
            $"Name of {id}",
            new PrefixComponentStatus(id, PrefixComponentState.Missing, $"no native {id} found"),
            [new PrefixDownload($"https://example.invalid/{id}/file.exe", $"hash-of-{id}", "file.exe")],
            [$"run file.exe /quiet for {id}"],
            WillInstall: true)).ToList();
        return new PrefixSetupPlan(
            new ResolvedPrefix(FileSystem.Shared.FromUnsanitizedFullPath(PrefixPath), CompatibilityLayerKind.SteamProton),
            entries,
            FileSystem.Shared.FromUnsanitizedFullPath(BackupPath),
            "This is the game's Steam prefix. Every Proton use of the game shares it.");
    }

    private GameInstallation Installation => Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First();

    private PrefixSetupViewModel NewViewModel(FakeRunner runner)
        => new(runner, Installation, MakePlan([Vcrun, D3d]), [Vcrun, D3d]) { Controller = Substitute.For<IOverlayController>() };

    private static PrefixSetupResult Succeeded(PrefixSetupRequest request) => new(
        MakePlan(request.Components),
        Written: true,
        request.Components.Select(id => new PrefixSetupStepResult(id, $"Name of {id}", PrefixSetupStepStatus.Installed)).ToList());

    [Fact]
    public async Task Test_PreviewShowsComponentsDownloadsHashesPrefixBackupAndSharedNote()
    {
        var vm = await OnUi(() => Task.FromResult(NewViewModel(new FakeRunner())));

        vm.PrefixPath.Should().Be(PrefixPath);
        vm.BackupPath.Should().Be(BackupPath);
        vm.SharedNote.Should().Contain("shares it");
        vm.Steps.Select(s => s.Heading).Should().Equal("Name of vcrun2022", "Name of d3dcompiler_47");
        var lines = vm.Steps[0].Lines;
        lines.Should().Contain(l => l.Contains("https://example.invalid/vcrun2022/file.exe") && l.Contains("hash-of-vcrun2022"));
        lines.Should().Contain(l => l.Contains("run file.exe /quiet for vcrun2022"));
        lines.Should().Contain(l => l.Contains("no native vcrun2022 found"));
    }

    [Fact]
    public async Task Test_TheConfirmationNamesThePrefixPath()
    {
        var vm = await OnUi(() => Task.FromResult(NewViewModel(new FakeRunner())));

        vm.ConfirmationText.Should().Contain(PrefixPath);
    }

    [Fact]
    public async Task Test_RunIsDisabledUntilTheConfirmationIsTicked()
    {
        var vm = await OnUi(() => Task.FromResult(NewViewModel(new FakeRunner())));

        (await OnUi(async () => await vm.RunCommand.CanExecute.FirstAsync())).Should().BeFalse();

        vm.IsConfirmed = true;
        (await OnUi(async () => await vm.RunCommand.CanExecute.FirstAsync())).Should().BeTrue();

        vm.IsConfirmed = false;
        (await OnUi(async () => await vm.RunCommand.CanExecute.FirstAsync())).Should().BeFalse();
    }

    [Fact]
    public async Task Test_RunWritesWithTheShownPathAsConfirmationAndShowsEachStep()
    {
        var runner = new FakeRunner();
        runner.OnWrite = async (request, stepFinished) =>
        {
            var result = Succeeded(request);
            foreach (var step in result.Steps) await stepFinished!(step);
            return result;
        };
        var vm = await OnUi(() => Task.FromResult(NewViewModel(runner)));
        vm.IsConfirmed = true;

        await OnUi(async () => await vm.RunCommand.Execute());

        var write = runner.Requests.Should().ContainSingle(r => r.Write).Subject;
        write.ConfirmedPrefixPath.Should().Be(vm.PrefixPath);
        write.Components.Should().Equal(Vcrun, D3d);
        vm.Progress.Should().Equal("Name of vcrun2022: Installed", "Name of d3dcompiler_47: Installed");
        vm.Succeeded.Should().BeTrue();
        vm.ErrorMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_AFailedStepShowsItsErrorAndRetryRunsTheSetupAgain()
    {
        var runner = new FakeRunner();
        var calls = 0;
        runner.OnWrite = async (request, stepFinished) =>
        {
            if (++calls > 1)
            {
                var done = Succeeded(request);
                foreach (var step in done.Steps) await stepFinished!(step);
                return done;
            }

            var steps = new List<PrefixSetupStepResult>
            {
                new(Vcrun, "Name of vcrun2022", PrefixSetupStepStatus.Failed, "installer exited with 5"),
                new(D3d, "Name of d3dcompiler_47", PrefixSetupStepStatus.NotRun),
            };
            foreach (var step in steps) await stepFinished!(step);
            return new PrefixSetupResult(MakePlan(request.Components), Written: true, steps);
        };
        var vm = await OnUi(() => Task.FromResult(NewViewModel(runner)));
        vm.IsConfirmed = true;

        await OnUi(async () => await vm.RunCommand.Execute());

        vm.Succeeded.Should().BeFalse();
        vm.ErrorMessage.Should().Contain("Name of vcrun2022").And.Contain("installer exited with 5");
        (await OnUi(async () => await vm.RetryCommand.CanExecute.FirstAsync())).Should().BeTrue();

        await OnUi(async () => await vm.RetryCommand.Execute());

        runner.Requests.Where(r => r.Write).Should().HaveCount(2).And.OnlyContain(r => r.ConfirmedPrefixPath == PrefixPath);
        vm.Succeeded.Should().BeTrue();
        vm.ErrorMessage.Should().BeEmpty();
        vm.Progress.Should().Equal("Name of vcrun2022: Installed", "Name of d3dcompiler_47: Installed");
    }

    [Fact]
    public async Task Test_ARefusalFromTheRunnerIsShownWithRetryInsteadOfThrown()
    {
        var runner = new FakeRunner();
        runner.OnWrite = (_, _) => throw new PrefixSetupRefusedException("The game is still running.");
        var vm = await OnUi(() => Task.FromResult(NewViewModel(runner)));
        vm.IsConfirmed = true;

        await OnUi(async () => await vm.RunCommand.Execute());

        vm.ErrorMessage.Should().Contain("The game is still running.");
        (await OnUi(async () => await vm.RetryCommand.CanExecute.FirstAsync())).Should().BeTrue();
    }

    [Fact]
    public async Task Test_RunAndRetryStayDisabledWhileASetupIsRunning()
    {
        var runner = new FakeRunner();
        var release = new TaskCompletionSource();
        runner.OnWrite = async (request, _) =>
        {
            await release.Task;
            return Succeeded(request);
        };
        var vm = await OnUi(() => Task.FromResult(NewViewModel(runner)));
        vm.IsConfirmed = true;

        var running = OnUi(async () => await vm.RunCommand.Execute());
        await Eventually(async () => (await OnUi(() => Task.FromResult(vm.IsRunning))).Should().BeTrue());

        (await OnUi(async () => await vm.RunCommand.CanExecute.FirstAsync())).Should().BeFalse();
        release.SetResult();
        await running;
        vm.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Test_ClosingTheDialogCancelsARunningSetup()
    {
        var runner = new FakeRunner();
        runner.OnWrite = async (request, _) =>
        {
            // Bounded, so a dialog that stops cancelling fails the test instead of hanging it.
            await Task.Delay(TimeSpan.FromSeconds(10), runner.LastToken);
            return Succeeded(request);
        };
        var vm = await OnUi(() => Task.FromResult(NewViewModel(runner)));
        vm.IsConfirmed = true;

        var running = OnUi(async () => await vm.RunCommand.Execute());
        await Eventually(() =>
        {
            lock (runner.Requests) runner.Requests.Should().Contain(r => r.Write);
        });

        await OnUi(() => vm.Close());
        await running;

        runner.LastToken.IsCancellationRequested.Should().BeTrue();
        vm.IsRunning.Should().BeFalse();
        vm.Succeeded.Should().BeFalse();
        vm.ErrorMessage.Should().Be(SilverModManager.App.UI.Resources.Language.PrefixSetup_Cancelled);
    }

    [Fact]
    public async Task Test_ADryRunRefusalLeavesTheHandlerWithoutOpeningThePreview()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Refused Prefix Loadout");
        var runner = new FakeRunner { DryRunRefusal = new PrefixSetupRefusedException("The prefix does not exist yet.") };
        var overlays = Substitute.For<IOverlayController>();
        var diagnostics = Substitute.For<IDiagnosticManager>();
        var handler = new SetUpPrefixActionHandler(new Services(Provider, overlays, runner), diagnostics);

        var act = () => handler.ExecuteAsync(loadout.LoadoutId, [Vcrun.Value]);

        await act.Should().ThrowAsync<PrefixSetupRefusedException>().WithMessage("The prefix does not exist yet.");
        runner.Requests.Should().ContainSingle().Which.Write.Should().BeFalse();
        await overlays.DidNotReceiveWithAnyArgs().EnqueueAndWait(Arg.Any<IOverlayViewModel<System.Reactive.Unit>>());
        await diagnostics.DidNotReceiveWithAnyArgs().RefreshAsync(default);
    }

    [Fact]
    public async Task Test_TheActionRunsTheDryRunForTheDiagnosticsComponentsAndOpensThePreview()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Prefix Setup Loadout");
        var runner = new FakeRunner();
        var overlays = Substitute.For<IOverlayController>();
        PrefixSetupViewModel? shown = null;
        overlays.EnqueueAndWait(Arg.Any<IOverlayViewModel<System.Reactive.Unit>>()).Returns(call =>
        {
            shown = (PrefixSetupViewModel)call.Arg<IOverlayViewModel<System.Reactive.Unit>>();
            return Task.FromResult<System.Reactive.Unit>(default);
        });
        var diagnostics = Substitute.For<IDiagnosticManager>();
        var handler = new SetUpPrefixActionHandler(new Services(Provider, overlays, runner), diagnostics);

        await handler.ExecuteAsync(loadout.LoadoutId, [Vcrun.Value, D3d.Value]);

        runner.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(new PrefixSetupRequest { Components = [Vcrun, D3d] });
        shown.Should().NotBeNull();
        shown!.PrefixPath.Should().Be(PrefixPath);
        await diagnostics.Received(1).RefreshAsync(loadout.LoadoutId);
    }

    [Fact]
    public void Test_TheHandlerIsRegisteredForTheSetUpPrefixAction()
    {
        Provider.GetServices<IDiagnosticActionHandler>().Should().ContainSingle(h => h.ActionId == DiagnosticActions.SetUpPrefixId);
    }

    private sealed class Services(IServiceProvider inner, IOverlayController overlays, IPrefixSetupRunner runner) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IOverlayController) ? overlays
                : serviceType == typeof(IPrefixSetupRunner) ? runner
                : inner.GetService(serviceType);
    }
}
