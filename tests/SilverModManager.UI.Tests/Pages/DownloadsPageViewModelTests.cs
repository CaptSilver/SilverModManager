using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Sdk.Jobs;
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
}
