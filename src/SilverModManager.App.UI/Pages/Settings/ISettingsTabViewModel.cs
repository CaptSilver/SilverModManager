using SilverModManager.App.UI.Controls.Settings.SettingEntries;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.App.UI.Pages.Settings;

public interface ISettingsTabViewModel : IViewModelInterface
{
    SettingsTab Tab { get; }
    string Name { get; }
    IconValue Icon { get; }

    /// <summary>Section headers (when the tab has more than one section) and rows, in display order.</summary>
    IReadOnlyList<IViewModelInterface> Items { get; }

    IReadOnlyList<ISettingEntryViewModel> Entries { get; }

    /// <summary>How many of this tab's rows hold an unsaved edit. Drives the header badge.</summary>
    R3.ReactiveProperty<int> ChangedCount { get; }
}
