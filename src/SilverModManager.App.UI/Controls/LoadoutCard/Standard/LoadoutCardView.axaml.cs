using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using SilverModManager.App.UI.Resources;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.LoadoutCard;

public partial class LoadoutCardView : ReactiveUserControl<ILoadoutCardViewModel>
{
    public LoadoutCardView()
    {
        InitializeComponent();

        // Wired once here rather than on activation, so reopening the page does not stack handlers. The box is set
        // back to what was stored afterwards because a dismissed dialog leaves the setting as it was.
        KeepSavesBox.Click += async (_, _) =>
        {
            if (ViewModel?.GameSettings is not { } settings) return;
            await settings.ToggleKeepSavesCommand.Execute();
            KeepSavesBox.IsChecked = settings.KeepSavesWithLoadout;
        };
        OwnInisBox.Click += async (_, _) =>
        {
            if (ViewModel?.GameSettings is not { } settings) return;
            await settings.ToggleOwnsGameInisCommand.Execute();
            OwnInisBox.IsChecked = settings.OwnsGameInis;
        };

        this.WhenActivated(d =>
            {
                // LoadoutBadge
                this.OneWayBind(ViewModel,
                        vm => vm.LoadoutBadgeViewModel,
                        view => view.LoadoutBadge.ViewModel)
                    .DisposeWith(d);
                
                // Game Image
                this.OneWayBind(ViewModel,
                        vm => vm.LoadoutImage,
                        view => view.GameImage.Source)
                    .DisposeWith(d);
                
                // Loadout Name
                this.OneWayBind(ViewModel,
                        vm => vm.LoadoutName,
                        view => view.LoadoutNameTextBlock.Text)
                    .DisposeWith(d);
                
                // Currently applied visibility
                this.OneWayBind(ViewModel,
                        vm => vm.IsLoadoutApplied,
                        view => view.CurrentlyAppliedStack.IsVisible)
                    .DisposeWith(d);
                
                // Last applied visibility
                this.WhenAnyValue(view => view.ViewModel!.IsLoadoutApplied)
                    .Select(isApplied => !isApplied)
                    .OnUI()
                    .BindToView(this, view => view.LastAppliedTimeTextBlock.IsVisible)
                    .DisposeWith(d);

                // Last applied time
                this.OneWayBind(ViewModel,
                        vm => vm.HumanizedLoadoutLastApplyTime,
                        view => view.LastAppliedTimeTextBlock.Text)
                    .DisposeWith(d);
                
                // Created time
                this.OneWayBind(ViewModel,
                        vm => vm.HumanizedLoadoutCreationTime,
                        view => view.CreatedTimeTextBlock.Text)
                    .DisposeWith(d);
                
                // Mod count
                this.OneWayBind(ViewModel,
                        vm => vm.LoadoutModCount,
                        view => view.NumberOfModsTextBlock.Text)
                    .DisposeWith(d);
                
                // Hide Delete button if last loadout
                Observable.CombineLatest(
                        this.WhenAnyValue(view => view.ViewModel!.IsLastLoadout),
                        this.WhenAnyValue(view => view.ViewModel!.IsLoadoutApplied),
                        (isLastLoadout, isApplied) => !isLastLoadout && !isApplied
                    )
                    .OnUI()
                    .BindToView(this, view => view.DeleteButton.IsVisible)
                    .DisposeWith(d);
                
                // Deleting state
                this.WhenAnyValue(view => view.ViewModel!.IsDeleting)
                    .OnUI()
                    .Subscribe(isDeleting =>
                    {
                        if (isDeleting) 
                            OverlayTextBlock.Text = Language.LoadoutCardViewDeletingText;
                        IsEnabled = !isDeleting;
                        OverlayStack.IsVisible = isDeleting;
                        ActionsDock.IsVisible = !isDeleting;
                    })
                    .DisposeWith(d);
                
                // Skeleton Creating state
                this.WhenAnyValue(view => view.ViewModel!.IsSkeleton)
                    .OnUI()
                    .Subscribe(isSkeleton =>
                    {
                        if (isSkeleton) 
                            OverlayTextBlock.Text = Language.LoadoutCardViewCreatingText;
                        IsEnabled = !isSkeleton;
                        OverlayStack.IsVisible = isSkeleton;
                        DetailsSectionStack.IsVisible = !isSkeleton;
                    })
                    .DisposeWith(d);

                // Saves and INI choices
                if (ViewModel?.GameSettings is { } settings)
                {
                    GameSettingsStack.IsVisible = true;
                    OwnInisBox.IsVisible = settings.ShowOwnsGameInis;
                    ToolTip.SetTip(KeepSavesBox, settings.KeepSavesToolTip);
                    settings.WhenAnyValue(x => x.KeepSavesWithLoadout)
                        .OnUI()
                        .Subscribe(keep => KeepSavesBox.IsChecked = keep)
                        .DisposeWith(d);
                    settings.WhenAnyValue(x => x.OwnsGameInis)
                        .OnUI()
                        .Subscribe(owns => OwnInisBox.IsChecked = owns)
                        .DisposeWith(d);
                    settings.ToggleKeepSavesCommand.CanExecute
                        .OnUI()
                        .Subscribe(can => KeepSavesBox.IsEnabled = can)
                        .DisposeWith(d);
                }

                // Clone loadout command
                this.BindCommand(ViewModel,
                        vm => vm.CloneLoadoutCommand,
                        view => view.CreateCopyButton)
                    .DisposeWith(d);
                
                // Delete loadout command
                this.BindCommand(ViewModel,
                        vm => vm.DeleteLoadoutCommand,
                        view => view.DeleteButton)
                    .DisposeWith(d);
                
                // Rename loadout command
                this.BindCommand(ViewModel,
                        vm => vm.RenameLoadoutCommand,
                        view => view.RenameButton)
                    .DisposeWith(d);

                // Only Steam installations can use the Steam launch option
                this.OneWayBind(ViewModel,
                        vm => vm.CanUseFromSteam,
                        view => view.SteamButton.IsVisible)
                    .DisposeWith(d);

                // Steam launch option command
                this.BindCommand(ViewModel,
                        vm => vm.UseFromSteamCommand,
                        view => view.SteamButton)
                    .DisposeWith(d);

                // Switching is offered on every loadout except the applied one
                this.OneWayBind(ViewModel,
                        vm => vm.ShowSwitchButton,
                        view => view.SwitchButton.IsVisible)
                    .DisposeWith(d);

                // Switch to loadout command
                this.BindCommand(ViewModel,
                        vm => vm.SwitchToLoadoutCommand,
                        view => view.SwitchButton)
                    .DisposeWith(d);

                // Visit loadout command
                this.BindCommand(ViewModel,
                        vm => vm.VisitLoadoutCommand,
                        view => view.CardOuterButton)
                    .DisposeWith(d);
            }
        );
    }
}

