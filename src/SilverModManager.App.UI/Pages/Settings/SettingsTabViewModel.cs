using SilverModManager.App.UI.Controls.Settings.Section;
using SilverModManager.App.UI.Controls.Settings.SettingEntries;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.UI.Sdk.Settings;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Settings;

public class SettingsTabViewModel : AViewModel<ISettingsTabViewModel>, ISettingsTabViewModel
{
    public SettingsTab Tab { get; }
    public string Name { get; }
    public IconValue Icon { get; }
    public IReadOnlyList<IViewModelInterface> Items { get; }
    public IReadOnlyList<ISettingEntryViewModel> Entries { get; }
    public R3.ReactiveProperty<int> ChangedCount { get; } = new();

    public SettingsTabViewModel(TabGroup<ISettingEntryViewModel> group)
    {
        Tab = group.Tab;
        (Name, Icon) = SettingsTabs.Describe(group.Tab);

        var items = new List<IViewModelInterface>();
        foreach (var section in group.Sections)
        {
            if (group.ShowsSectionHeaders) items.Add(new SettingSectionViewModel(section.Section));
            items.AddRange(section.Rows);
        }

        Items = items;
        Entries = group.Sections.SelectMany(section => section.Rows).ToArray();

        // Same lifetime argument as the page's own merge: the rows live as long as this view model.
        System.Reactive.Linq.Observable.Merge(Entries
                .Select(vm => vm.WhenAnyValue(x => x.InteractionControlViewModel.ValueContainer.HasChanged)))
            .SubscribeWithErrorLogging(_ =>
            {
                ChangedCount.Value = Entries.Count(e => e.InteractionControlViewModel.ValueContainer.HasChanged);
            });
    }
}
