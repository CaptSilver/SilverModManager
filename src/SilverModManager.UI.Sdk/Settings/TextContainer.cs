using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Sdk.Settings;

[PublicAPI]
public class TextContainer : APropertyValueContainer<string, TextContainerOptions>
{
    public TextContainer(
        string value,
        string defaultValue,
        PropertyConfig config) : base(value, defaultValue, config) { }
}
