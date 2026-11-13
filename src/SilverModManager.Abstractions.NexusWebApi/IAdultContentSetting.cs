using R3;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Abstractions.NexusWebApi;

/// <summary>
/// The app-wide "Show adult content" choice. Every browse page and the CLI read it from here, so they can't
/// disagree about it and the pages react the moment it changes.
/// </summary>
public interface IAdultContentSetting
{
    bool ShowAdultContent { get; }

    /// <summary>The current value on subscribing, then each time it changes.</summary>
    IObservable<bool> Changes { get; }
}

/// <inheritdoc/>
public sealed class SettingsAdultContentSetting(ISettingsManager settings) : IAdultContentSetting
{
    public bool ShowAdultContent => settings.Get<AdultContentSettings>().ShowAdultContent;

    public IObservable<bool> Changes => settings.GetChanges<AdultContentSettings>(prependCurrent: true)
        .Select(static s => s.ShowAdultContent)
        .DistinctUntilChanged()
        .AsSystemObservable();
}
