using FluentAssertions;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Overlays.Login;
using NexusMods.Sdk.Jobs;
using NSubstitute;
using System.Windows.Input;
using R3;

namespace NexusMods.UI.Tests.Overlays;

public class ApiKeyLoginOverlayTests
{
    private static ApiKeyLoginOverlayViewModel CreateViewModel()
    {
        return new ApiKeyLoginOverlayViewModel { Controller = Substitute.For<IOverlayController>() };
    }

    [Fact]
    public void SaveIsDisabledWhileTheKeyBoxIsEmpty()
    {
        var viewModel = CreateViewModel();

        CanExecute(viewModel.CommandSave).Should().BeFalse();

        viewModel.ApiKey.Value = "   ";
        CanExecute(viewModel.CommandSave).Should().BeFalse("whitespace is not a key");

        viewModel.ApiKey.Value = "abc";
        CanExecute(viewModel.CommandSave).Should().BeTrue();
    }

    [Fact]
    public void SaveCompletesWithTheTrimmedKey()
    {
        var viewModel = CreateViewModel();
        viewModel.ApiKey.Value = "  abc \n";

        viewModel.CommandSave.Execute(Unit.Default);

        viewModel.Result.Should().Be("abc");
        viewModel.CompletionTask.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void CancelCompletesWithoutAKey()
    {
        var viewModel = CreateViewModel();
        viewModel.ApiKey.Value = "abc";

        viewModel.CommandCancel.Execute(Unit.Default);

        viewModel.Result.Should().BeNull();
        viewModel.CompletionTask.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task PromptReturnsTheKeyTheOverlayCompletedWith()
    {
        var controller = Substitute.For<IOverlayController>();
        controller.EnqueueAndWait(Arg.Any<IOverlayViewModel<string>>()).Returns("typed-key");

        var key = await new ApiKeyPrompt(controller).PromptAsync(CancellationToken.None);

        key.Should().Be("typed-key");
    }

    [Fact]
    public async Task PromptReturnsNullWhenTheOverlayIsCancelled()
    {
        var controller = Substitute.For<IOverlayController>();
        controller.EnqueueAndWait(Arg.Any<IOverlayViewModel<string>>()).Returns((string?)null);

        var key = await new ApiKeyPrompt(controller).PromptAsync(CancellationToken.None);

        key.Should().BeNull();
    }

    [Fact]
    public async Task UseApiKeyCancelsTheBrowserFlowThenOpensTheKeyDialog()
    {
        var job = Substitute.For<IJob>();
        job.Id.Returns(JobId.From(Guid.NewGuid()));
        var jobMonitor = Substitute.For<IJobMonitor>();
        var loginManager = Substitute.For<ILoginManager>();
        var viewModel = new NexusLoginOverlayViewModel(job, jobMonitor, loginManager) { Controller = Substitute.For<IOverlayController>() };

        viewModel.UseApiKey.Execute(Unit.Default);

        jobMonitor.Received(1).Cancel(job.Id);
        await loginManager.Received(1).LoginWithApiKeyAsync(Arg.Any<CancellationToken>());
        viewModel.Status.Should().Be(Status.Closed);
    }

    [Fact]
    public void CancelStopsTheBrowserFlow()
    {
        var job = Substitute.For<IJob>();
        job.Id.Returns(JobId.From(Guid.NewGuid()));
        var jobMonitor = Substitute.For<IJobMonitor>();
        var viewModel = new NexusLoginOverlayViewModel(job, jobMonitor, Substitute.For<ILoginManager>()) { Controller = Substitute.For<IOverlayController>() };

        viewModel.Cancel.Execute(Unit.Default);

        jobMonitor.Received(1).Cancel(job.Id);
    }

    private static bool CanExecute(ICommand command) => command.CanExecute(null);
}
