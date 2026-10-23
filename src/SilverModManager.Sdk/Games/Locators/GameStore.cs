using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;
using TransparentValueObjects;

namespace SilverModManager.Sdk.Games;

// TODO: use string hash cache

/// <summary>
/// Represents a game store from which the game installation originates.
/// </summary>
[ValueObject<string>]
[PublicAPI]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public readonly partial struct GameStore
{
    /// <summary>
    /// Unknown.
    /// </summary>
    public static readonly GameStore Unknown = From("Unknown");

    /// <summary>
    /// Steam.
    /// </summary>
    public static readonly GameStore Steam = From("Steam");

    /// <summary>
    /// GOG.
    /// </summary>
    public static readonly GameStore GOG = From("GOG");

    /// <summary>
    /// EGS.
    /// </summary>
    public static readonly GameStore EGS = From("Epic Games Store");

    /// <summary>
    /// Amazon Games.
    /// </summary>
    public static readonly GameStore Amazon = From("Amazon");

    /// <summary>
    /// Lutris, for a game that was added to it by hand rather than installed from a store it syncs with.
    /// </summary>
    public static readonly GameStore Lutris = From("Lutris");

    /// <summary>
    /// Manually added.
    /// </summary>
    public static readonly GameStore ManuallyAdded = From("Manually Added");
}

/// <summary>
/// An attribute that contains the name of a game store.
/// </summary>
public class GameStoreAttribute(string ns, string name) : ScalarAttribute<GameStore, string, AsciiSerializer>(ns, name)
{
    /// <inheritdoc />
    protected override string ToLowLevel(GameStore value) => value.Value;

    /// <inheritdoc />
    protected override GameStore FromLowLevel(string value, AttributeResolver resolver) => GameStore.From(value);
}
