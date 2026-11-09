namespace SilverModManager.Games.TestFramework.Helpers;

/// <summary>
/// The bytes of a native executable's stand-in: padding around a VS_FIXEDFILEINFO, which is all the version is read
/// from. There is no managed metadata, so the reader in NexusMods.Paths has nothing to take a version from on Linux.
/// </summary>
public static class NativeExecutable
{
    public static byte[] WithFileVersion(Version version) => WithFileVersion((uint)(version.Major << 16 | version.Minor), (uint)(version.Build << 16 | version.Revision));

    public static byte[] WithFileVersion(uint fileVersionMs, uint fileVersionLs)
    {
        var bytes = new byte[400];
        Array.Fill(bytes, (byte)0xAB);
        BitConverter.TryWriteBytes(bytes.AsSpan(100), 0xFEEF04BDu);
        BitConverter.TryWriteBytes(bytes.AsSpan(104), 0x00010000u);
        BitConverter.TryWriteBytes(bytes.AsSpan(108), fileVersionMs);
        BitConverter.TryWriteBytes(bytes.AsSpan(112), fileVersionLs);
        return bytes;
    }
}
