namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// The algorithm a <see cref="FileIdentity"/>'s digest was made with. Values are stored, so they never change
/// meaning; a value this build does not know is carried and compared as-is, never reinterpreted.
/// <see cref="MinimalHash"/> (xxHash3 of three blocks) is deliberately absent: it samples a file, it does not identify it.
/// </summary>
public enum HashKind : byte
{
    /// <summary>No identity.</summary>
    None = 0,

    /// <summary>xxHash3 (64 bit), the hash the file store is keyed by.</summary>
    XxHash3 = 1,

    /// <summary>SHA-1, the hash a Steam depot manifest carries.</summary>
    Sha1 = 2,

    /// <summary>MD5.</summary>
    Md5 = 3,

    /// <summary>SHA-256.</summary>
    Sha256 = 4,

    /// <summary>xxHash64.</summary>
    XxHash64 = 5,

    /// <summary>CRC-32.</summary>
    Crc32 = 6,
}
