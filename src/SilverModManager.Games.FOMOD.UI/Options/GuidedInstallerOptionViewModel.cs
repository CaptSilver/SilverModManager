using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.App.UI;
using SilverModManager.UI.Sdk;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.Games.FOMOD.UI;

public partial class GuidedInstallerOptionViewModel : AViewModel<IGuidedInstallerOptionViewModel>, IGuidedInstallerOptionViewModel
{
    public Option Option { get; }
    public OptionGroup Group { get; }

    [Reactive] public partial bool IsEnabled { get; set; }
    [Reactive] public partial bool IsChecked { get; set; }
    [Reactive] public partial bool IsValid { get; set; } = true;

    public GuidedInstallerOptionViewModel(Option option, OptionGroup group)
    {
        Option = option;
        Group = group;
        IsEnabled = option.Type is not OptionType.Disabled and not OptionType.Required;
        IsChecked = option.Type is OptionType.PreSelected or OptionType.Required;
    }
}
