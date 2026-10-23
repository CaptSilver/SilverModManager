using SilverModManager.MnemonicDB.Abstractions.ElementComparers;

namespace SilverModManager.MnemonicDB.Abstractions.DatomComparators;

/// <summary>
/// VAE Comparator.
/// </summary>
public class VAEComparator : APartialDatomComparator<ValueComparer, AComparer, EComparer>;
