using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.UI.Sdk.Settings;

[PublicAPI]
public record SectionDescriptor(
    SectionId Id,
    string Name,
    Func<IconValue> IconFunc,
    uint Priority = 0,
    bool Hidden = false
);
