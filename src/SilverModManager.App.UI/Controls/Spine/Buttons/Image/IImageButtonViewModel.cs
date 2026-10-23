using System.Reactive;
using Avalonia.Media;
using SilverModManager.App.UI.Controls.LoadoutBadge;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.Spine.Buttons.Image;

public interface IImageButtonViewModel : ISpineItemViewModel
{

    /// <summary>
    /// Name for the tooltip on the button
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Image for the button
    /// </summary>
    public IImage Image { get; set; }
    
    /// <summary>
    /// ViewModel for the loadout badge
    /// </summary>
    public ILoadoutBadgeViewModel? LoadoutBadgeViewModel { get; set; }

    /// <summary>
    /// Moves this button one place up the spine; the keyboard path for drag and drop.
    /// </summary>
    public ReactiveCommand<Unit, Unit>? MoveUp { get; set; }

    /// <summary>
    /// Moves this button one place down the spine.
    /// </summary>
    public ReactiveCommand<Unit, Unit>? MoveDown { get; set; }
}
