using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.App.UI.Controls.Settings.Section;

public interface ISettingSectionViewModel : IViewModelInterface
{
    SectionDescriptor Descriptor { get; }
}
