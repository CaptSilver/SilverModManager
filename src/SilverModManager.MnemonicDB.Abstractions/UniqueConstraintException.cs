using System;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;

namespace SilverModManager.MnemonicDB.Abstractions;

/// <summary>
/// Thrown when a unique constraint on a attribute is
/// </summary>
public class UniqueConstraintException(Datom datom) : Exception($"Unique constraint violation on datom: {datom}");
