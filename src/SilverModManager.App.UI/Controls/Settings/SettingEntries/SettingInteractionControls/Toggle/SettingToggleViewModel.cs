using System.Reactive.Disposables;
using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Settings;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

public partial class SettingToggleViewModel : AViewModel<ISettingToggleViewModel>, ISettingToggleViewModel
{
    public BooleanContainer BooleanContainer { get; }

    public IPropertyValueContainer ValueContainer => BooleanContainer;

    [Reactive] public partial bool HasChanged { get; private set;  }

    public SettingToggleViewModel(BooleanContainer booleanContainer)
    {
        BooleanContainer = booleanContainer;

        this.WhenActivated(disposables =>
        {
            ValueContainer.WhenAnyValue(x => x.HasChanged)
                .BindToVM(this, vm => vm.HasChanged)
                .DisposeWith(disposables);
        });
    }
}

[UsedImplicitly]
public class SettingToggleFactory : IInteractionControlFactory<BooleanContainerOptions>
{
    public IInteractionControl Create(IServiceProvider serviceProvider, ISettingsManager settingsManager, BooleanContainerOptions containerOptions, PropertyConfig propertyConfig)
    {
        return new SettingToggleViewModel(
            new BooleanContainer(
                value: propertyConfig.GetValueCasted<bool>(settingsManager),
                defaultValue: propertyConfig.GetDefaultValueCasted<bool>(settingsManager),
                config: propertyConfig
            )
        );
    }
}
