namespace NexusMods.Wabbajack.Transforms;

/// <summary>
/// A DDS texture uses a format or layout the re-encoder cannot read or write.
/// </summary>
public sealed class UnsupportedTextureFormatException(string message) : Exception(message);
