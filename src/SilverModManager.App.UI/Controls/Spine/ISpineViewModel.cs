using System.Collections.ObjectModel;
using SilverModManager.App.UI.Controls.Spine.Buttons.Download;
using SilverModManager.App.UI.Controls.Spine.Buttons.Icon;
using SilverModManager.App.UI.Controls.Spine.Buttons.Image;
using SilverModManager.App.UI.LeftMenu;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Controls.Spine;

public interface ISpineViewModel : IViewModelInterface
{
    /// <summary>
    /// Gets the left menu view model.
    /// </summary>
    public ILeftMenuViewModel? LeftMenuViewModel { get; }

    /// <summary>
    /// Gets the home button.
    /// </summary>
    public IIconButtonViewModel Home { get; }
    
    /// <summary>
    /// Gets the add loadout button.
    /// </summary>
    public IIconButtonViewModel AddLoadout { get; }

    /// <summary>
    /// Gets the downloads button.
    /// </summary>
    public ISpineDownloadButtonViewModel Downloads { get; }

    /// <summary>
    /// Gets all loadout buttons.
    /// </summary>
    public ReadOnlyObservableCollection<IImageButtonViewModel> LoadoutSpineItems { get; }

    public void NavigateToHome();

    /// <summary>
    /// Moves the loadout button at <paramref name="fromIndex"/> of <see cref="LoadoutSpineItems"/> to
    /// <paramref name="toIndex"/> and saves the resulting order.
    /// </summary>
    public void MoveLoadout(int fromIndex, int toIndex);
}
