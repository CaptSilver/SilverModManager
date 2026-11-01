using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// Runs a set of <see cref="IDestinationInference"/> in priority order. The startup migration and the row's
/// "Suggest a game" action both ask this, so they cannot disagree about which evidence wins.
/// </summary>
[PublicAPI]
public static class DestinationInferences
{
    /// <summary>Sorts the inferences once, for a caller that asks about many items.</summary>
    public static IReadOnlyList<IDestinationInference> InPriorityOrder(IEnumerable<IDestinationInference> inferences)
        => inferences.OrderBy(inference => inference.Priority).ToArray();

    /// <summary>
    /// The first non-empty answer, or <see cref="Destination.Unknown"/> when no evidence says what game the item is for.
    /// </summary>
    public static Destination Infer(IReadOnlyList<IDestinationInference> inPriorityOrder, IDb db, LibraryItem.ReadOnly item)
        => inPriorityOrder.Select(inference => inference.Infer(db, item)).FirstOrDefault(found => !found.IsUnknown) ?? Destination.Unknown;
}
