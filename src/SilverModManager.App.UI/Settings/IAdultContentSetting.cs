using System.Reactive.Linq;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Settings;

/// <summary>
/// The app-wide "Show adult content" choice. Every browse page reads it from here, so the pages can't disagree
/// about it and all react the moment it changes.
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

    public IObservable<bool> Changes => R3.ObservableExtensions
        .AsSystemObservable(settings.GetChanges<AdultContentSettings>(prependCurrent: true))
        .Select(static s => s.ShowAdultContent)
        .DistinctUntilChanged();
}
