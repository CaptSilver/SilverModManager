using System.Reactive.Linq;
using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Settings;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

public partial class SettingComboBoxViewModel : AViewModel<ISettingComboBoxViewModel>, ISettingComboBoxViewModel
{
    public IPropertyValueContainer ValueContainer => SingleValueMultipleChoiceContainer;
    public SingleValueMultipleChoiceContainer SingleValueMultipleChoiceContainer { get; }

    public string[] DisplayItems { get; }

    [Reactive] public partial int SelectedItemIndex { get; set; }

    public SettingComboBoxViewModel(SingleValueMultipleChoiceContainer valueContainer)
    {
        SingleValueMultipleChoiceContainer = valueContainer;

        DisplayItems = valueContainer.Values.Select(x => x.Value).ToArray();
        SelectedItemIndex = GetIndex(valueContainer.CurrentValue);

        // The container is the single source of truth. Both directions stay wired for the view model's life, not only
        // while a view shows it: a tab that is hidden still receives Save and Discard through the container.
        this.WhenAnyValue(x => x.SelectedItemIndex)
            .Select(GetValue)
            .BindToVM(this, vm => vm.SingleValueMultipleChoiceContainer.CurrentValue);

        this.WhenAnyValue(x => x.SingleValueMultipleChoiceContainer.CurrentValue)
            .Select(GetIndex)
            .BindToVM(this, vm => vm.SelectedItemIndex);
    }

    private object GetValue(int index)
    {
        var values = SingleValueMultipleChoiceContainer.Values;
        if (index == -1) return values.First().Key;
        if (index >= values.Length) return values.Last().Key;
        return values[index].Key;
    }

    private int GetIndex(object value)
    {
        for (var i = 0; i < SingleValueMultipleChoiceContainer.Values.Length; i++)
        {
            var other = SingleValueMultipleChoiceContainer.Values[i];
            if (SingleValueMultipleChoiceContainer.EqualityComparer.Equals(other.Key, value))
                return i;
        }

        return -1;
    }
}

[UsedImplicitly]
public class SettingComboBoxFactory : IInteractionControlFactory<SingleValueMultipleChoiceContainerOptions>
{
    public IInteractionControl Create(IServiceProvider serviceProvider, ISettingsManager settingsManager, SingleValueMultipleChoiceContainerOptions containerOptions, PropertyConfig propertyConfig)
    {
        return new SettingComboBoxViewModel(
            valueContainer: new SingleValueMultipleChoiceContainer(
                value: propertyConfig.GetValue(settingsManager),
                defaultValue: propertyConfig.GetDefaultValue(settingsManager),
                config: propertyConfig,
                options: containerOptions
            )
        );
    }
}
