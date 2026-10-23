using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.Internals;
using SilverModManager.MnemonicDB.Storage.Abstractions.ElementComparers;

namespace SilverModManager.MnemonicDB.Abstractions.DatomComparators;

/// <summary>
/// The AVET Comparator.
/// </summary>
public sealed class AVETComparator : ADatomComparator<
    AComparer,
    ValueComparer,
    EComparer,
    TxComparer>;
