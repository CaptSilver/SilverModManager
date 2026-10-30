using System.Reactive.Disposables;
using DynamicData;
using JetBrains.Annotations;
using Microsoft.Extensions.Hosting;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.App.UI.Overlays.Login;

/// <summary>
/// Shows the "waiting for your browser" overlay while a forum sign-in job runs, and closes it when the job ends.
/// </summary>
[UsedImplicitly]
public class ForumLoginOverlayService : IHostedService
{
    private readonly IOverlayController _overlayController;
    private readonly IJobMonitor _jobMonitor;
    private readonly IOSInterop _os;
    private readonly CompositeDisposable _compositeDisposable = new();

    private ForumLoginOverlayViewModel? _overlayViewModel;

    public ForumLoginOverlayService(IOverlayController overlayController, IJobMonitor jobMonitor, IOSInterop os)
    {
        _overlayController = overlayController;
        _jobMonitor = jobMonitor;
        _os = os;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _jobMonitor
            .ObserveActiveJobs<IForumLoginJob>()
            .OnUI()
            .SubscribeWithErrorLogging(changeSet =>
            {
                if (changeSet.Removes > 0) _overlayViewModel?.Close();

                if (changeSet.Adds > 0)
                {
                    _overlayViewModel?.Close();

                    var job = changeSet.First(x => x.Reason == ChangeReason.Add).Current;
                    _overlayViewModel = new ForumLoginOverlayViewModel(job, _jobMonitor, _os);
                    _overlayController.Enqueue(_overlayViewModel);
                }
            })
            .DisposeWith(_compositeDisposable);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _overlayViewModel?.Close();
        _compositeDisposable.Dispose();
        return Task.CompletedTask;
    }
}
