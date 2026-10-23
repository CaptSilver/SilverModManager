using System;
using System.Runtime.CompilerServices;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.Internals;
using SilverModManager.MnemonicDB.Storage.Abstractions.ElementComparers;

namespace SilverModManager.MnemonicDB.Abstractions.DatomComparators;

/// <summary>
/// The VAET Comparator.
/// </summary>
public sealed class VAETComparator : ADatomComparator<
    ValueComparer,
    AComparer,
    EComparer,
    TxComparer>
{
}

