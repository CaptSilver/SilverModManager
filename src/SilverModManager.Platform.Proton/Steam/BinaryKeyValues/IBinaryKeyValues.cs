namespace SilverModManager.Platform.Proton.Steam.BinaryKeyValues;

/// <summary>
/// Reads and writes Steam's binary KeyValues files (<c>shortcuts.vdf</c>) keeping each value's type byte. SteamKit2
/// turns every leaf into a string on save, which would retype <c>appid</c> and every flag.
/// </summary>
public interface IBinaryKeyValues
{
    /// <summary>
    /// Parses the file into an unnamed root map holding its top-level nodes.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are truncated or use a type this reader does not know.</exception>
    BinaryKvMap Read(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// The bytes of <paramref name="root"/> in the form <see cref="Read"/> accepts.
    /// </summary>
    byte[] Write(BinaryKvMap root);
}
