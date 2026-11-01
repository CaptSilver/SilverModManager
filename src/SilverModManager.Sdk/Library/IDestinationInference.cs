using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// Works out a game for an item that was added before destinations were recorded, from one kind of evidence.
/// Also behind the row's "Suggest a game" action.
/// </summary>
[PublicAPI]
public interface IDestinationInference
{
    /// <summary>Lower runs first.</summary>
    int Priority { get; }

    /// <summary>What this evidence says about the item, or <see cref="Destination.Unknown"/> when it has none.</summary>
    Destination Infer(IDb db, LibraryItem.ReadOnly item);
}
