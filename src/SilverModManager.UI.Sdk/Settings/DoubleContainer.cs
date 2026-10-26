using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Sdk.Settings;

[PublicAPI]
public class DoubleContainer : APropertyValueContainer<double, SliderContainerOptions>
{
    public SliderContainerOptions Options { get; }

    public DoubleContainer(
        double value,
        double defaultValue,
        PropertyConfig config,
        SliderContainerOptions options) : base(value, defaultValue, config)
    {
        Options = options;
    }
}
