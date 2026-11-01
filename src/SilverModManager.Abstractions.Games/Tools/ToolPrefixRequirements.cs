using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>
/// A Wine registry value set for one exe before it runs: <c>HKCU\Software\Wine\AppDefaults\&lt;ExeName&gt;</c>,
/// value <see cref="Value"/> set to <see cref="Data"/>.
/// </summary>
public sealed record PrefixRegistryDefault(string ExeName, string Value, string Data);

/// <summary>What a tool needs in its Wine prefix before it can run.</summary>
public sealed record ToolPrefixRequirements
{
    public static ToolPrefixRequirements None { get; } = new();

    /// <summary>Components the prefix setup installs, such as the Visual C++ runtime.</summary>
    public IReadOnlyList<PrefixComponentId> Components { get; init; } = [];

    public IReadOnlyList<PrefixRegistryDefault> RegistryDefaults { get; init; } = [];
}
