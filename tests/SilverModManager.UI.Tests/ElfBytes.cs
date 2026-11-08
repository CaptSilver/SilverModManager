using System.Runtime.InteropServices;
using SilverModManager.Sdk;

namespace SilverModManager.UI.Tests;

/// <summary>
/// A minimal 64-byte ELF header for a CPU: the ident bytes plus <c>e_machine</c>, which is all the updater's CPU check reads.
/// </summary>
internal static class ElfBytes
{
    public static byte[] For(Architecture architecture, byte filler = 0)
    {
        var machine = ReleaseAssetArchitecture.ElfMachine(architecture) ?? throw new ArgumentOutOfRangeException(nameof(architecture));
        return ForMachine(machine, filler);
    }

    public static byte[] ForMachine(ushort machine, byte filler = 0)
    {
        var bytes = new byte[64];
        Array.Fill(bytes, filler);
        byte[] ident = [0x7f, (byte)'E', (byte)'L', (byte)'F', 0x02, 0x01, 0x01];
        ident.CopyTo(bytes, 0);
        bytes[18] = (byte)(machine & 0xff);
        bytes[19] = (byte)(machine >> 8);
        return bytes;
    }
}
