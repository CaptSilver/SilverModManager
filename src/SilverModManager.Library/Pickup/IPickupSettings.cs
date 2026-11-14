using R3;

namespace SilverModManager.Library.Pickup;

/// <summary>
/// What the user chose about picking finished browser downloads up from their Downloads folder.
/// </summary>
public interface IPickupSettings
{
    /// <summary>Whether finished browser downloads are picked up. Off unless the user turned it on.</summary>
    bool PickUpFromDownloads { get; }

    /// <summary>The folder to watch; empty means the XDG download folder.</summary>
    string DownloadsFolder { get; }

    /// <summary>Raised after either setting changed.</summary>
    Observable<Unit> Changes { get; }
}
