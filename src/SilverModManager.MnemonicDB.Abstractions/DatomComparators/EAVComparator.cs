using SilverModManager.MnemonicDB.Abstractions.ElementComparers;

namespace SilverModManager.MnemonicDB.Abstractions.DatomComparators;

/// <summary>
/// EAV Comparator.
/// </summary>
public class EAVComparator : APartialDatomComparator<EComparer, AComparer, ValueComparer>;
