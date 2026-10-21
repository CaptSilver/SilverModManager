using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;

namespace NexusMods.Sdk.Library;

/// <summary>
/// Added to a library item to hold it on the file it is: the update check reports nothing newer for a
/// frozen item, and nothing moves its pin.
/// </summary>
[PublicAPI]
public partial class UpdatePin : IModelDefinition
{
    private const string Namespace = "SilverModManager.Library.UpdatePin";

    /// <summary>
    /// The item must stay on its current file.
    /// </summary>
    public static readonly BooleanAttribute Frozen = new(Namespace, nameof(Frozen));
}

public partial class UpdatePin
{
    /// <summary>
    /// Whether the library item with id <paramref name="item"/> is frozen. An item that was never given an
    /// <see cref="UpdatePin"/> is not.
    /// </summary>
    public static bool IsFrozen(IDb db, EntityId item)
    {
        var pin = Load(db, item);
        return pin.IsValid() && pin.Frozen;
    }
}
