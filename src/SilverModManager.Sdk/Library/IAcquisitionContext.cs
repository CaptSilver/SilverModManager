using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// Where an item being added to the library is going. Each way of acquiring a file implements it once; the
/// library writes the answer on the item in the same transaction that creates it.
/// </summary>
[PublicAPI]
public interface IAcquisitionContext
{
    /// <summary>The games the item is for, read from the snapshot the add runs against.</summary>
    Destination Destination(IDb db);
}
