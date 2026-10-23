namespace NexusMods.Wabbajack.Transforms;

/// <summary>
/// A delta could not be applied: it is corrupt, truncated, or does not belong to the basis.
/// The message carries Octodiff's own text, which names what it tripped over.
/// </summary>
public sealed class PatchException(string message, Exception inner) : Exception(message, inner);
