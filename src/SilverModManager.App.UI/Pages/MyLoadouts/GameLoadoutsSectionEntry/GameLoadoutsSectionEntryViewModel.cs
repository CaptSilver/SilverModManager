using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.LoadoutCard;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;

public class GameLoadoutsSectionEntryViewModel : AViewModel<IGameLoadoutsSectionEntryViewModel>, IGameLoadoutsSectionEntryViewModel
{
    private static readonly CardViewModelComparer CardComparerInstance = new();
    private readonly CompositeDisposable _compositeDisposable;
    private readonly GameInstallation _gameInstallation;
    private readonly IWindowManager _windowManager;
    private readonly ReadOnlyObservableCollection<IViewModelInterface> _loadoutCardViewModels;
    private readonly ReadOnlyObservableCollection<IViewModelInterface> _cardViewModels;
    private readonly SourceList<IViewModelInterface> _cardViewModelsSourceList = new();
    public string HeadingText { get; }
    public ReadOnlyObservableCollection<IViewModelInterface> CardViewModels => _cardViewModels;


    /// <summary>
    /// Whether the loadout is visible and sits on the install. This runs inside the database's event loop, where
    /// an exception stops the commit that triggered it from ever finishing, so a loadout whose game is no longer
    /// found is left out rather than thrown on.
    /// </summary>
    internal static bool IsOnInstallation(IGameRegistry gameRegistry, Loadout.ReadOnly loadout, GameInstallation installation) =>
        loadout.IsVisible()
        && gameRegistry.TryGetGameInstallation(loadout, out var found)
        && found.Locations[LocationId.Game].Path == installation.Locations[LocationId.Game].Path;

    public GameLoadoutsSectionEntryViewModel(GameInstallation gameInstallation, IConnection conn, IServiceProvider serviceProvider, IWindowManager windowManager)
    {
        _compositeDisposable = new CompositeDisposable();
        _gameInstallation = gameInstallation;
        _windowManager = windowManager;

        var loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();
        HeadingText = string.Format(Language.MyLoadoutsGameSectionHeading, _gameInstallation.Game.DisplayName);

        var gameRegistry = serviceProvider.GetRequiredService<IGameRegistry>();
        Loadout.ObserveAll(conn)
            .Filter(l => IsOnInstallation(gameRegistry, l, _gameInstallation))
            .Transform(loadout =>
                {
                    return (IViewModelInterface)new LoadoutCardViewModel(loadout, conn, serviceProvider)
                    {
                        VisitLoadoutCommand = ReactiveCommand.Create(() => NavigateToLoadout(loadout)),
                    };
                }
            )
            .Bind(out _loadoutCardViewModels)
            .Subscribe()
            .DisposeWith(_compositeDisposable);

        var createNewLoadoutCard = new CreateNewLoadoutCardViewModel()
        {
            AddLoadoutCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                await loadoutManager.CreateLoadout(_gameInstallation);
            }),
        };

        _cardViewModelsSourceList.Add(createNewLoadoutCard);

        _cardViewModelsSourceList.Connect()
            .Merge(_loadoutCardViewModels.ToObservableChangeSet())
            .Sort(CardComparerInstance)
            .OnUI()
            .Bind(out _cardViewModels)
            .SubscribeWithErrorLogging()
            .DisposeWith(_compositeDisposable);
    }

    private class CardViewModelComparer : IComparer<IViewModelInterface>
    {
        public int Compare(IViewModelInterface? x, IViewModelInterface? y)
        {
            if (x == null) return y == null ? 0 : -1;
            if (y == null) return 1;

            // Prioritize CreateNewLoadoutCardViewModel to always come first.
            if (x is CreateNewLoadoutCardViewModel) return -1;
            if (y is CreateNewLoadoutCardViewModel) return 1;

            return (x, y) switch
            {
                (LoadoutCardViewModel lx, LoadoutCardViewModel ly) => DateTimeOffset.Compare(lx.LoadoutVal.GetCreatedAt(), ly.LoadoutVal.GetCreatedAt()),
                (SkeletonLoadoutCardViewModel sx, SkeletonLoadoutCardViewModel sy) => string.Compare(sx.LoadoutName, sy.LoadoutName, StringComparison.Ordinal),
                (LoadoutCardViewModel lxs, SkeletonLoadoutCardViewModel sys) => -1,
                (SkeletonLoadoutCardViewModel sxs, LoadoutCardViewModel lys) => 1,
                _ => 0,
            };
        }
    }


    private void NavigateToLoadout(Loadout.ReadOnly loadout)
    {
        var loadoutId = loadout.LoadoutId;
        Dispatcher.UIThread.Invoke(() =>
            {
                var workspaceController = _windowManager.ActiveWorkspaceController;

                workspaceController.ChangeOrCreateWorkspaceByContext(
                    context => context.LoadoutId == loadoutId,
                    () => new PageData
                    {
                        FactoryId = LoadoutPageFactory.StaticId,
                        Context = new LoadoutPageContext
                        {
                            LoadoutId = loadoutId,
                            GroupScope = Optional<CollectionGroupId>.None,
                        },
                    },
                    () => new LoadoutContext
                    {
                        LoadoutId = loadoutId,
                    }
                );
            }
        );
    }

    public void Dispose()
    {
        _compositeDisposable.Dispose();
    }
}
