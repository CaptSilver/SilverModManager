using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Storage.Abstractions.ElementComparers;

namespace SilverModManager.MnemonicDB.Abstractions.DatomComparators;

/// <summary>
/// The EAVT Comparator.
/// </summary>
public sealed class EAVTComparator : ADatomComparator<
    EComparer,
    AComparer,
    ValueComparer,
    TxComparer>;

