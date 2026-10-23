using Avalonia.Media;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;

namespace SilverModManager.App.UI.WorkspaceSystem;

public class NewTabPageSectionItemViewModel : AViewModel<INewTabPageSectionItemViewModel>, INewTabPageSectionItemViewModel
{
    public string SectionName { get; }

    public string Name { get; }

    public IconValue Icon { get; }

    public ReactiveCommand<NavigationInformation, ValueTuple<PageData, NavigationInformation>> SelectItemCommand { get; }

    public NewTabPageSectionItemViewModel(PageDiscoveryDetails details)
    {
        SectionName = details.SectionName;
        Name = details.ItemName;
        Icon = details.Icon;

        SelectItemCommand = ReactiveCommand.Create<NavigationInformation, ValueTuple<PageData, NavigationInformation>>(info => (details.PageData, info));
    }
}
