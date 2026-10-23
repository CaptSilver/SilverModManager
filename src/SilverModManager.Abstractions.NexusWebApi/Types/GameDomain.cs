using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;
using TransparentValueObjects;
namespace SilverModManager.Abstractions.NexusWebApi.Types;

/// <summary>
/// Machine friendly name for the game, should be devoid of special characters
/// that may conflict with URLs or file paths.
///
/// Sometimes used as a Game ID.
/// </summary>
/// <remarks>
///    Usually we match these with NexusMods' URLs.
/// </remarks>
[ValueObject<string>]
public readonly partial struct GameDomain : IAugmentWith<DefaultValueAugment, JsonAugment, DefaultEqualityComparerAugment>
{
    /// <inheritdoc/>
    public static GameDomain DefaultValue { get; } = From("Unknown");

    /// <inheritdoc/>
    public static IEqualityComparer<string> InnerValueDefaultEqualityComparer { get; } = StringComparer.OrdinalIgnoreCase;
}

/// <summary>
/// MnemonicDB attribute for the GameDomain type.
/// </summary>
public class GameDomainAttribute(string ns, string name) : ScalarAttribute<GameDomain, string, AsciiSerializer>(ns, name)
{
    /// <inheritdoc />
    protected override string ToLowLevel(GameDomain value)
    {
        return value.Value;
    }

    /// <inheritdoc />
    protected override GameDomain FromLowLevel(string value, AttributeResolver resolver)
    {
        return GameDomain.From(value);
    }
}
