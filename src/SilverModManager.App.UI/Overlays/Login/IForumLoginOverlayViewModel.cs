namespace SilverModManager.App.UI.Overlays.Login;

public interface IForumLoginOverlayViewModel : IOverlayViewModel
{
    /// <summary>The forum being signed in to, as the user knows it.</summary>
    string SiteName { get; }

    /// <summary>The page the browser was sent to, once it has been opened.</summary>
    Uri? Uri { get; }

    /// <summary>Gives up on the sign-in.</summary>
    R3.ReactiveCommand Cancel { get; }

    /// <summary>Opens <see cref="Uri"/> again, for a browser that did not come up.</summary>
    R3.ReactiveCommand OpenAgain { get; }
}
