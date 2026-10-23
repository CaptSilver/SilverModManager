using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Sdk.Settings;

[PublicAPI]
public interface IInteractionControl : IViewModelInterface
{
    IPropertyValueContainer ValueContainer { get; }
}

[PublicAPI]
public interface IInteractionControlFactory<in TContainerOptions>
    where TContainerOptions : IContainerOptions
{
    IInteractionControl Create(IServiceProvider serviceProvider, ISettingsManager settingsManager, TContainerOptions containerOptions, PropertyConfig propertyConfig);
}
