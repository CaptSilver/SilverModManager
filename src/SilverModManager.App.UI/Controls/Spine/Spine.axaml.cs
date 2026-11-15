using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using ReactiveUI;
using SilverModManager.App.UI.Input.Reorder;

namespace SilverModManager.App.UI.Controls.Spine;

public partial class Spine : ReactiveUserControl<ISpineViewModel>
{
    public Spine()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            // The pad picks loadout icons up through the spine's reorderable.
            ReorderSources.SetSource(this, ViewModel?.Reorderable is { } reorderable ? new ReorderSource<SilverModManager.Sdk.Loadouts.LoadoutId>(reorderable, ReorderRows.InSpine, TimeProvider.System) : null);
            Disposable.Create(() => ReorderSources.SetSource(this, null)).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.Home, v => v.Home.ViewModel)
                .DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.LoadoutSpineItems, v => v.Loadouts.ItemsSource)
                .DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.Downloads, v => v.Download.ViewModel)
                .DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.AddLoadout, v => v.Add.ViewModel)
                .DisposeWith(disposables);

            // The panels between the icons and the spine clip to their bounds, so unclipping runs up to the spine itself.
            new VerticalReorderDrag(Loadouts, LoadoutSelectorScroller, _ => true, (from, to) => ViewModel?.MoveLoadout(from, to), ReorderDragFeel.Spine, unclipUpTo: this)
                .DisposeWith(disposables);
        });
    }
}
