namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// A DDS texture uses a format or layout the re-encoder cannot read or write.
/// </summary>
public class UnsupportedTextureFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The source DDS is truncated or corrupt, as opposed to using a layout we do not handle. Callers that keep
/// the original for unsupported textures keep it here too, but word the reason differently.
/// </summary>
public sealed class UnreadableTextureException(string message, Exception? inner = null) : UnsupportedTextureFormatException(message, inner);
