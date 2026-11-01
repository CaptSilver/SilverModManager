namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>Where one window's XDND conversation stands.</summary>
public enum XdndPhase
{
    /// <summary>No drag is over the window.</summary>
    Idle,

    /// <summary>A source has entered and offered its types; nothing has been requested from it.</summary>
    Entered,

    /// <summary>The file list has been requested and a Status is owed to the source until it arrives.</summary>
    FetchingData,

    /// <summary>The window has the file list and has been told of the drag; positions go to it.</summary>
    Ready,
}
