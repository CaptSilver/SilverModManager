using System.Collections.Immutable;
using JetBrains.Annotations;

namespace SilverModManager.Sdk;

[PublicAPI]
public readonly record struct WineDllOverride(string DllName, ImmutableArray<WineDllOverrideType> OverrideTypes)
{
    /// <summary>
    /// Gets whether the DLL is disabled.
    /// </summary>
    public bool IsDisabled => OverrideTypes is [WineDllOverrideType.Disabled];

    /// <summary>
    /// Gets whether Wine tries a native DLL of this name before its own builtin, so a file in the game folder loads.
    /// </summary>
    public bool PrefersNative => OverrideTypes is [WineDllOverrideType.Native, ..];

    internal static readonly ImmutableArray<WineDllOverrideType> Disabled = [WineDllOverrideType.Disabled];

    /// <inheritdoc/>
    public override string ToString()
    {
        var typesString = OverrideTypes.Select(ToString).Aggregate((a, b) => $"{a},{b}");
        return $"{DllName}={typesString}";
    }

    private static string ToString(WineDllOverrideType overrideType) => overrideType switch
    {
        WineDllOverrideType.BuiltIn => "b",
        WineDllOverrideType.Native => "n",
        _ => "",
    };
}
