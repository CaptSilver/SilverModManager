using System.Collections.ObjectModel;
using SilverModManager.App.UI.Controls.Spine.Buttons.Download;
using SilverModManager.App.UI.Controls.Spine.Buttons.Icon;
using SilverModManager.App.UI.Controls.Spine.Buttons.Image;
using SilverModManager.App.UI.LeftMenu;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Controls.Spine;

public class SpineDesignViewModel : AViewModel<ISpineViewModel>, ISpineViewModel
{
    public ILeftMenuViewModel? LeftMenuViewModel => null;

    public IIconButtonViewModel Home { get; } = new IconButtonDesignViewModel();
    public IIconButtonViewModel AddLoadout { get; } = new IconButtonDesignViewModel();

    public ISpineDownloadButtonViewModel Downloads { get; } = new SpineDownloadButtonDesignerViewModel();

    public ReadOnlyObservableCollection<IImageButtonViewModel> LoadoutSpineItems { get; } =
        new(new ObservableCollection<IImageButtonViewModel>
    {
        new ImageButtonDesignViewModel(),
        new ImageButtonDesignViewModel(),
        new ImageButtonDesignViewModel(),
        new ImageButtonDesignViewModel(),
    });

    public void NavigateToHome() { }

    public void MoveLoadout(int fromIndex, int toIndex) { }
}
