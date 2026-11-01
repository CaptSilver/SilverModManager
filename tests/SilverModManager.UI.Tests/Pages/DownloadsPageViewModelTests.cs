using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Sdk.Jobs;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.TreeDataGrid;
using SilverModManager.App.UI.Pages.Downloads;
using SilverModManager.App.UI.Windows;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>The page's bulk and selection commands exist from construction, whether or not the page is shown.</summary>
public class DownloadsPageViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<DownloadsPageViewModel> CreateViewModel(IDownloadsService? downloadsService = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(downloadsService ?? Substitute.For<IDownloadsService>())
            .AddSingleton(Substitute.For<IDownloadsDataProvider>())
            .AddSingleton(Substitute.For<SilverModManager.Sdk.Settings.ISettingsManager>())
            .BuildServiceProvider();
        return await OnUi(() => Task.FromResult(
            new DownloadsPageViewModel(Substitute.For<IWindowManager>(), services, new DownloadsPageContext())));
    }

    [Fact]
    public async Task Commands_ExistAndCannotRunBeforeThePageIsActivated()
    {
        var viewModel = await CreateViewModel();

        IReadOnlyList<R3.ReactiveCommand<R3.Unit>> commands =
        [
            viewModel.PauseAllCommand, viewModel.ResumeAllCommand, viewModel.PauseSelectedCommand,
            viewModel.ResumeSelectedCommand, viewModel.CancelSelectedCommand,
        ];

        commands.Should().OnlyContain(command => command != null);
        commands.Should().OnlyContain(command => !command.CanExecute());
    }

    [Fact]
    public async Task PauseAllCommand_CanRunOnlyWhileActiveAndADownloadIsRunning()
    {
        var running = new DownloadInfo { Id = DownloadId.From(Guid.NewGuid()) };
        running.SetStatus(JobStatus.Running);
        running.SetSupportsPausing(true);
        var downloads = new SourceCache<DownloadInfo, DownloadId>(x => x.Id);
        downloads.AddOrUpdate(running);
        var service = Substitute.For<IDownloadsService>();
        service.AllDownloads.Returns(downloads.Connect());
        var viewModel = await CreateViewModel(service);

        var activation = await OnUi(() => Task.FromResult(viewModel.Activator.Activate()));
        await EventuallyOnUi(() => viewModel.PauseAllCommand.CanExecute().Should().BeTrue());

        await OnUi(() => { activation.Dispose(); return Task.CompletedTask; });
        await EventuallyOnUi(() => viewModel.PauseAllCommand.CanExecute().Should().BeFalse());
    }

    [Fact]
    public async Task PauseAllCommand_CannotRunWhenOnlyDownloadsThatCannotPauseAreRunning()
    {
        var running = new DownloadInfo { Id = DownloadId.From(Guid.NewGuid()) };
        running.SetStatus(JobStatus.Running);
        running.SetSupportsPausing(false);
        var downloads = new SourceCache<DownloadInfo, DownloadId>(x => x.Id);
        downloads.AddOrUpdate(running);
        var service = Substitute.For<IDownloadsService>();
        service.AllDownloads.Returns(downloads.Connect());
        var viewModel = await CreateViewModel(service);

        await OnUi(() => { viewModel.Activator.Activate(); return Task.CompletedTask; });
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await OnUi(() => { viewModel.PauseAllCommand.CanExecute().Should().BeFalse(); return Task.CompletedTask; });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PauseSelectedCommand_CanRunOnlyWhenASelectedRunningDownloadCanPause(bool supportsPausing)
    {
        var viewModel = await CreateViewModel();
        var model = new CompositeItemModel<DownloadId>(DownloadId.From(Guid.NewGuid()));
        var status = new R3.BehaviorSubject<JobStatus>(JobStatus.Running);
        model.Add(DownloadColumns.Status.ComponentKey, new DownloadComponents.StatusComponent(
            new Percent(0), JobStatus.Running, R3.Observable.Never<Percent>(), status,
            supportsPausing, null, R3.Observable.Never<string?>()));

        await OnUi(() => { viewModel.Activator.Activate(); viewModel.Adapter.SelectedModels.Add(model); return Task.CompletedTask; });
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await OnUi(() => { viewModel.PauseSelectedCommand.CanExecute().Should().Be(supportsPausing); return Task.CompletedTask; });
    }
}
