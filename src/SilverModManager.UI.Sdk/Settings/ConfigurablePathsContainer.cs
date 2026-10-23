using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Sdk.Settings;

public class ConfigurablePathsContainer : APropertyValueContainer<ConfigurablePath[], ConfigurablePathsContainerOption>
{
    public ConfigurablePathsContainer(
        ConfigurablePath[] value,
        ConfigurablePath[] defaultValue,
        PropertyConfig config) : base(value, defaultValue, config)
    {
    }
}
