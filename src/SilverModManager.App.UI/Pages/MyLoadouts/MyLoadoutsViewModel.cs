using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.MyLoadouts;

public class MyLoadoutsViewModel : APageViewModel<IMyLoadoutsViewModel>, IMyLoadoutsViewModel
{
    private ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> _gameSectionViewModels = new([]);

    public ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> GameSectionViewModels => _gameSectionViewModels;

    public MyLoadoutsViewModel(
        IWindowManager windowManager,
        IConnection conn,
        IServiceProvider serviceProvider) : base(windowManager)
    {
        TabTitle = Language.MyLoadoutsPageTitle;
        TabIcon = IconValues.Package;
        
        this.WhenActivated(d =>
        {
            Loadout.ObserveAll(conn)
                .Filter(l => l.IsVisible())
                .DistinctValues(loadout => loadout.InstallationInstance)
                .Transform(managedGameInstall =>
                    {
                        return (IGameLoadoutsSectionEntryViewModel)new GameLoadoutsSectionEntryViewModel(
                            managedGameInstall,
                            conn,
                            serviceProvider,
                            windowManager
                        );
                    }
                )
                .OnUI()
                .Bind(out _gameSectionViewModels)
                // These entries are not used as actual vms, just as data source for DataTemplates in a ItemsControl,
                // so they need manual disposal as there is no WhenActivated mechanism for them
                .DisposeMany()
                .SubscribeWithErrorLogging()
                .DisposeWith(d);
        });
    }

}
