using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Sdk.Settings;

[PublicAPI]
public class BooleanContainer : APropertyValueContainer<bool, BooleanContainerOptions>
{
    public BooleanContainer(
        bool value,
        bool defaultValue,
        PropertyConfig config) : base(value, defaultValue, config) { }
}
