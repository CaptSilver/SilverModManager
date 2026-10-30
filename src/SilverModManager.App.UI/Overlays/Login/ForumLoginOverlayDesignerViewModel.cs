namespace SilverModManager.App.UI.Overlays.Login;

public class ForumLoginOverlayDesignerViewModel : AOverlayViewModel<IForumLoginOverlayViewModel>, IForumLoginOverlayViewModel
{
    public string SiteName => "LoversLab";
    public R3.ReactiveCommand Cancel { get; } = new();
    public R3.ReactiveCommand OpenAgain { get; } = new();
    public Uri Uri { get; } = new("https://www.loverslab.com/oauth/authorize/?client_id=example");
}
