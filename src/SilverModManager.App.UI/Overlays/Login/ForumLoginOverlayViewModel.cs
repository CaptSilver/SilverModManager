using R3;
using ReactiveUI.SourceGenerators;
using SilverModManager.Networking.Forums;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.App.UI.Overlays.Login;

public partial class ForumLoginOverlayViewModel : AOverlayViewModel<IForumLoginOverlayViewModel>, IForumLoginOverlayViewModel
{
    public ForumLoginOverlayViewModel(IJob job, IJobMonitor jobMonitor, IOSInterop os)
    {
        if (job.Definition is IForumLoginJob forumJob)
        {
            SiteName = forumJob.Site.DisplayName();
            forumJob.LoginUri
                .ObserveOnUIThreadDispatcher()
                .Subscribe(this, static (uri, self) => self.Uri = uri);
        }

        // The job keeps waiting for the browser until it is cancelled.
        Cancel = new ReactiveCommand(execute: _ =>
        {
            jobMonitor.Cancel(job.Id);
            Close();
        });

        OpenAgain = new ReactiveCommand(execute: _ =>
        {
            if (Uri is { } uri) os.OpenUri(uri);
        });
    }

    public string SiteName { get; } = "the forum";
    public ReactiveCommand Cancel { get; }
    public ReactiveCommand OpenAgain { get; }
    [Reactive] public partial Uri? Uri { get; private set; }
}
