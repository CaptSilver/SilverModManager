using System.Reactive;
using Avalonia.Media;
using SilverModManager.App.UI.Controls.LoadoutBadge;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.LoadoutCard;

public class SkeletonLoadoutCardViewModel : AViewModel<ILoadoutCardViewModel>, ILoadoutCardViewModel 
{
    public ILoadoutBadgeViewModel LoadoutBadgeViewModel { get; } = new LoadoutBadgeDesignViewModel();
    public required string LoadoutName { get; init; }
    public required IImage LoadoutImage { get; init; } 
    public bool IsLoadoutApplied { get; } = false;
    public string HumanizedLoadoutLastApplyTime { get; } = "";
    public string HumanizedLoadoutCreationTime { get; } = "";
    public string LoadoutModCount { get; } = "";
    public bool IsDeleting { get; } = false;
    public bool IsSkeleton { get; } = true;
    public bool IsLastLoadout { get; } = false;
    public ReactiveCommand<Unit, Unit> VisitLoadoutCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> CloneLoadoutCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> DeleteLoadoutCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> RenameLoadoutCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> UseFromSteamCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> SwitchToLoadoutCommand { get; } = ReactiveCommand.Create(() => { });
    public bool ShowSwitchButton { get; } = false;
    public bool CanUseFromSteam { get; } = false;
    public LoadoutGameSettings? GameSettings { get; } = null;
}
