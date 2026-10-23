using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.Internals;
using SilverModManager.MnemonicDB.Storage.Abstractions.ElementComparers;

namespace SilverModManager.MnemonicDB.Abstractions.DatomComparators;

/// <summary>
/// The AEVT Comparator.
/// </summary>
public sealed class AEVTComparator : ADatomComparator<
    AComparer,
    EComparer,
    ValueComparer,
    TxComparer>;
