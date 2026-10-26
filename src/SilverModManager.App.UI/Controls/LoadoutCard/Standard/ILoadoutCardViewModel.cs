using System.Reactive;
using Avalonia.Media;
using SilverModManager.App.UI.Controls.LoadoutBadge;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.LoadoutCard;

public interface ILoadoutCardViewModel : IViewModelInterface
{
    ILoadoutBadgeViewModel LoadoutBadgeViewModel { get; }
    
    string LoadoutName { get; }
    
    IImage? LoadoutImage { get; }
    
    bool IsLoadoutApplied { get; }
    
    string HumanizedLoadoutLastApplyTime { get; }
    
    string HumanizedLoadoutCreationTime { get; }
    
    string LoadoutModCount { get; }
    
    bool IsDeleting { get; }
    
    bool IsSkeleton { get; }
    
    bool IsLastLoadout { get; }
    
    ReactiveCommand<Unit, Unit> VisitLoadoutCommand { get; }
    
    ReactiveCommand<Unit, Unit> CloneLoadoutCommand { get; }
    
    ReactiveCommand<Unit, Unit> DeleteLoadoutCommand { get; }

    ReactiveCommand<Unit, Unit> RenameLoadoutCommand { get; }

    ReactiveCommand<Unit, Unit> UseFromSteamCommand { get; }

    /// <summary>
    /// Applies this loadout to its game, then opens it. Not available while a synchronize runs on the same installation.
    /// </summary>
    ReactiveCommand<Unit, Unit> SwitchToLoadoutCommand { get; }

    /// <summary>
    /// Whether to offer <see cref="SwitchToLoadoutCommand"/>: the loadout that is already applied has nothing to switch to.
    /// </summary>
    bool ShowSwitchButton { get; }

    /// <summary>
    /// Whether the loadout's game was installed through Steam; only then can Steam launch with it.
    /// </summary>
    bool CanUseFromSteam { get; }

    /// <summary>
    /// The loadout's choices about the game's saves and INI files. Null on a card that stands for a loadout still being created.
    /// </summary>
    LoadoutGameSettings? GameSettings { get; }
}
