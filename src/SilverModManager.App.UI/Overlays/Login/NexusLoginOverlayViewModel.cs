using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Sdk.Jobs;
using R3;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Overlays.Login;

public partial class NexusLoginOverlayViewModel : AOverlayViewModel<INexusLoginOverlayViewModel>, INexusLoginOverlayViewModel
{
    public NexusLoginOverlayViewModel(IJob job, IJobMonitor jobMonitor, ILoginManager loginManager)
    {
        if (job.Definition is IOAuthJob oAuthJob)
        {
            oAuthJob.LoginUriSubject
                .ObserveOnUIThreadDispatcher()
                .Subscribe(this, static (uri, self) => self.Uri = uri);
        }

        // The job keeps waiting for the browser until it is cancelled, and LoginAsync waits on the job.
        Cancel = new ReactiveCommand(execute: _ =>
        {
            jobMonitor.Cancel(job.Id);
            Close();
        });

        UseApiKey = new ReactiveCommand<Unit>(executeAsync: async (_, cancellationToken) =>
        {
            jobMonitor.Cancel(job.Id);
            Close();
            await loginManager.LoginWithApiKeyAsync(cancellationToken);
        }, awaitOperation: AwaitOperation.Drop);
    }

    public ReactiveCommand Cancel { get; }
    public ReactiveCommand<Unit> UseApiKey { get; }
    [Reactive] public partial Uri? Uri { get; private set; }
}
