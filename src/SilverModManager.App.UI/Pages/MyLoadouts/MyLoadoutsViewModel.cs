using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.MyLoadouts;

public class MyLoadoutsViewModel : APageViewModel<IMyLoadoutsViewModel>, IMyLoadoutsViewModel
{
    private ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> _gameSectionViewModels = new([]);

    public ReadOnlyObservableCollection<IGameLoadoutsSectionEntryViewModel> GameSectionViewModels => _gameSectionViewModels;

    public bool IsFilteredToGame { get; }

    public string FilterHeadingText { get; } = "";

    public ReactiveCommand<Unit, Unit> ShowAllGamesCommand { get; }

    private void ShowAllGames()
    {
        var pageData = new PageData
        {
            FactoryId = MyLoadoutsPageFactory.StaticId,
            Context = new MyLoadoutsPageContext(),
        };
        GetWorkspaceController().OpenPage(WorkspaceId, pageData, new OpenPageBehavior.ReplaceTab(PanelId, TabId));
    }

    public MyLoadoutsViewModel(
        IWindowManager windowManager,
        IConnection conn,
        IServiceProvider serviceProvider,
        GameInstallMetadataId? installationFilter = null) : base(windowManager)
    {
        TabTitle = Language.MyLoadoutsPageTitle;
        TabIcon = IconValues.Package;

        IsFilteredToGame = installationFilter.HasValue;
        if (installationFilter is { } filteredId)
        {
            // The name comes from the install's own row, so it is there even once its last loadout is gone.
            var installation = GameInstallMetadata.Load(conn.Db, filteredId);
            FilterHeadingText = string.Format(Language.MyLoadoutsFilteredHeading, installation.IsValid() ? installation.Name : "");
        }

        ShowAllGamesCommand = ReactiveCommand.Create(ShowAllGames);

        var gameRegistry = serviceProvider.GetRequiredService<IGameRegistry>();

        this.WhenActivated(d =>
        {
            Loadout.ObserveAll(conn)
                // A loadout whose game can't be found would make the section transform throw and end the stream,
                // taking every other game's section off the page with it.
                .Filter(l => l.IsVisible() && (installationFilter is null || l.InstallationId == installationFilter) && gameRegistry.TryGetGameInstallation(l, out _))
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
