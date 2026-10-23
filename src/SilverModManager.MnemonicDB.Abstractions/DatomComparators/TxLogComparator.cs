using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.Internals;
using SilverModManager.MnemonicDB.Storage.Abstractions.ElementComparers;

namespace SilverModManager.MnemonicDB.Abstractions.DatomComparators;

/// <summary>
/// The TxLog Comparator.
/// </summary>
public sealed class TxLogComparator : ADatomComparator<
    TxComparer,
    EComparer,
    AComparer,
    ValueComparer>;
