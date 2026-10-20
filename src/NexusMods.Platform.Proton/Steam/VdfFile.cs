using NexusMods.Paths;
using SteamKit2;

namespace NexusMods.Platform.Proton.Steam;

internal static class VdfFile
{
    /// <summary>
    /// Parses a VDF file. Returns null when the file is missing or malformed, so callers treat both as "not configured".
    /// </summary>
    public static KeyValue? Read(AbsolutePath path)
    {
        if (!path.FileExists) return null;

        using var stream = path.Read();
        using var reader = new StreamReader(stream);
        return KeyValue.LoadFromString(reader.ReadToEnd());
    }
}
