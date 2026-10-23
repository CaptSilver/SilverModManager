using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.App.UI.Controls.Settings.Section;

public class SettingSectionViewModel : AViewModel<ISettingSectionViewModel>, ISettingSectionViewModel
{
    public SectionDescriptor Descriptor { get; }

    public SettingSectionViewModel(SectionDescriptor descriptor)
    {
        Descriptor = descriptor;
    }
}
