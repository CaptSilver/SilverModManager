using System;

namespace SilverModManager.MnemonicDB.Abstractions;

/// <summary>
/// Thrown when a transaction retracts a value its entity does not hold in the database the transaction runs
/// against. The whole transaction is refused: committing it would leave the value current and write a retraction
/// of nothing into history.
/// </summary>
public class RetractionMismatchException(string message) : Exception(message);
