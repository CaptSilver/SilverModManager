using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Aggregation;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.LoadoutBadge;

public partial class LoadoutBadgeViewModel : AViewModel<ILoadoutBadgeViewModel>, ILoadoutBadgeViewModel
{
 
    [Reactive] public partial Optional<Loadout.ReadOnly> LoadoutValue { get; set; }
    
    public LoadoutBadgeViewModel(IConnection conn, ISynchronizerService syncService, bool hideOnSingleLoadout = false)
    {
        this.WhenActivated(d =>
        {
            var applyStatusSerialDisposable = new SerialDisposable().DisposeWith(d);
            
            this.WhenAnyValue(vm => vm.LoadoutValue)
                .Where(l => l.HasValue)
                .Select(l => l!.Value)
                .Do(loadout =>
                {
                    LoadoutShortName = loadout.ShortName;
                    
                    applyStatusSerialDisposable.Disposable = Observable.FromAsync(() => syncService.StatusForLoadout(loadout.LoadoutId))
                    .Switch()
                    .OnUI()
                    .Do(applyStatus =>
                        {
                            IsLoadoutApplied = applyStatus is LoadoutSynchronizerState.Current or LoadoutSynchronizerState.NeedsSync;
                            IsLoadoutInProgress = applyStatus is LoadoutSynchronizerState.Pending;
                        }
                    )
                    .SubscribeWithErrorLogging();;
                })
                .SubscribeWithErrorLogging()
                .DisposeWith(d);
            
            if (hideOnSingleLoadout)
            {
                var startingLoadouts = Loadout.All(conn.Db);
                Loadout.ObserveAll(conn)
                    .Filter(l => l.InstallationId == LoadoutValue.Value.InstallationId)
                    .Count()
                    .Select(count => count > 1)
                    .OnUI()
                    .SubscribeWithErrorLogging(isVisible => IsVisible = isVisible)
                    .DisposeWith(d);
            }
        });
    }
    [Reactive] public partial string LoadoutShortName { get; private set; } = " ";
    [Reactive] public partial bool IsLoadoutApplied { get; private set; } = false;
    [Reactive] public partial bool IsLoadoutInProgress { get; set; } = false;
    [Reactive] public partial bool IsLoadoutSelected { get; set; } = true;
    [Reactive] public partial bool IsVisible { get; set; } = true;
}
