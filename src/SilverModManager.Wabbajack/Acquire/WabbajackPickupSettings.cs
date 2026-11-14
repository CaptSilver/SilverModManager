using R3;
using SilverModManager.Library.Pickup;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// The Downloads pickup's settings, kept on <see cref="WabbajackSettings"/> so nobody's opt-in moves.
/// </summary>
public sealed class WabbajackPickupSettings(ISettingsManager settings) : IPickupSettings
{
    /// <inheritdoc />
    public bool PickUpFromDownloads => settings.Get<WabbajackSettings>().PickUpFromDownloads;

    /// <inheritdoc />
    public string DownloadsFolder => settings.Get<WabbajackSettings>().DownloadsFolder;

    /// <inheritdoc />
    public Observable<Unit> Changes => settings.GetChanges<WabbajackSettings>().Select(_ => Unit.Default);
}
