using System.Reactive.Disposables;
using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Settings;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

public partial class SettingTextViewModel : AViewModel<ISettingTextViewModel>, ISettingTextViewModel
{
    public TextContainer TextContainer { get; }

    public IPropertyValueContainer ValueContainer => TextContainer;

    [Reactive] public partial bool HasChanged { get; private set; }

    public SettingTextViewModel(TextContainer textContainer)
    {
        TextContainer = textContainer;

        this.WhenActivated(disposables =>
        {
            ValueContainer.WhenAnyValue(x => x.HasChanged)
                .BindToVM(this, vm => vm.HasChanged)
                .DisposeWith(disposables);
        });
    }
}

[UsedImplicitly]
public class SettingTextFactory : IInteractionControlFactory<TextContainerOptions>
{
    public IInteractionControl Create(IServiceProvider serviceProvider, ISettingsManager settingsManager, TextContainerOptions containerOptions, PropertyConfig propertyConfig)
    {
        return new SettingTextViewModel(
            new TextContainer(
                value: propertyConfig.GetValueCasted<string>(settingsManager),
                defaultValue: propertyConfig.GetDefaultValueCasted<string>(settingsManager),
                config: propertyConfig
            )
        );
    }
}
