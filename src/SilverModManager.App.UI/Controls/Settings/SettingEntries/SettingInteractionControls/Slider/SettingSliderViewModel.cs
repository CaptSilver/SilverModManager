using System.Reactive.Disposables;
using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Settings;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

public partial class SettingSliderViewModel : AViewModel<ISettingSliderViewModel>, ISettingSliderViewModel
{
    public DoubleContainer DoubleContainer { get; }

    public IPropertyValueContainer ValueContainer => DoubleContainer;

    [Reactive] public partial bool HasChanged { get; private set; }

    [Reactive] public partial string ValueText { get; private set; }

    public SettingSliderViewModel(DoubleContainer doubleContainer)
    {
        DoubleContainer = doubleContainer;
        ValueText = doubleContainer.Options.ValueToDisplayString(doubleContainer.CurrentValue);

        this.WhenActivated(disposables =>
        {
            ValueContainer.WhenAnyValue(x => x.HasChanged)
                .BindToVM(this, vm => vm.HasChanged)
                .DisposeWith(disposables);

            DoubleContainer.WhenAnyValue(x => x.CurrentValue)
                .Subscribe(value => ValueText = DoubleContainer.Options.ValueToDisplayString(value))
                .DisposeWith(disposables);
        });
    }
}

[UsedImplicitly]
public class SettingSliderFactory : IInteractionControlFactory<SliderContainerOptions>
{
    public IInteractionControl Create(IServiceProvider serviceProvider, ISettingsManager settingsManager, SliderContainerOptions containerOptions, PropertyConfig propertyConfig)
    {
        return new SettingSliderViewModel(
            new DoubleContainer(
                value: propertyConfig.GetValueCasted<double>(settingsManager),
                defaultValue: propertyConfig.GetDefaultValueCasted<double>(settingsManager),
                config: propertyConfig,
                options: containerOptions
            )
        );
    }
}
