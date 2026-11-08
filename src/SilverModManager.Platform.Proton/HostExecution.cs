using System.Globalization;
using System.Runtime.InteropServices;

namespace SilverModManager.Platform.Proton;

/// <summary>Whether this machine can start x86-64 programs, and how.</summary>
public enum X86_64Support
{
    /// <summary>The process itself is x86-64.</summary>
    Native,

    /// <summary>An enabled binfmt_misc entry hands x86-64 ELF files to an interpreter such as FEX.</summary>
    Interpreter,

    /// <summary>The binfmt_misc table was read and no enabled entry takes x86-64 ELF files.</summary>
    None,

    /// <summary>The binfmt_misc table could not be read, so nothing is known.</summary>
    Unknown,
}

/// <summary>
/// Answers whether x86-64 programs can run on this machine, by process architecture and by the kernel's
/// binfmt_misc table. On an x86-64 process nothing is read.
/// </summary>
/// <param name="binfmtDirectory">The folder holding one file per registered format, normally <see cref="DefaultBinfmtDirectory"/>. Tests point it at a scratch folder.</param>
/// <param name="processArchitecture">The architecture of this process.</param>
public sealed class HostExecution(string binfmtDirectory, Architecture processArchitecture)
{
    /// <summary>Where the kernel lists registered binary formats.</summary>
    public const string DefaultBinfmtDirectory = "/proc/sys/fs/binfmt_misc";

    // Bytes of an ELF header that say "x86-64 ELF file": the ident and the low and high byte of e_machine.
    private static readonly (int Position, byte Value)[] X86_64Header =
    [
        (0, 0x7f), (1, 0x45), (2, 0x4c), (3, 0x46), (18, 0x3e), (19, 0x00),
    ];

    private const int MachineLowByte = 18;

    /// <summary>The folder this probe reads, for messages.</summary>
    public string BinfmtDirectory => binfmtDirectory;

    /// <summary>The probe for this process, reading the real kernel table.</summary>
    public static HostExecution ForThisProcess() => new(DefaultBinfmtDirectory, RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// Whether an x86-64 program can be started from this process.
    /// </summary>
    public X86_64Support CanRunX86_64()
    {
        if (processArchitecture == Architecture.X64) return X86_64Support.Native;

        try
        {
            foreach (var file in Directory.EnumerateFiles(binfmtDirectory))
            {
                var name = Path.GetFileName(file);
                if (name is "register" or "status") continue;
                if (TakesX86_64(File.ReadAllLines(file))) return X86_64Support.Interpreter;
            }
        }
        catch (Exception e) when (e is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return X86_64Support.Unknown;
        }

        return X86_64Support.None;
    }

    // The entry is a few "key value" lines: enabled/disabled first, then interpreter, flags, offset, magic, mask.
    private static bool TakesX86_64(string[] lines)
    {
        if (lines.Length == 0 || lines[0].Trim() != "enabled") return false;

        var offset = 0;
        byte[]? magic = null;
        byte[]? mask = null;
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;

            switch (parts[0])
            {
                case "offset":
                    if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out offset)) return false;
                    break;
                case "magic":
                    magic = ParseHex(parts[1]);
                    if (magic is null) return false;
                    break;
                case "mask":
                    mask = ParseHex(parts[1]);
                    if (mask is null) return false;
                    break;
            }
        }

        if (magic is null) return false;
        mask ??= Enumerable.Repeat((byte)0xff, magic.Length).ToArray();
        if (mask.Length != magic.Length) return false;

        // An entry that does not look at e_machine takes ELF files of every CPU, which says nothing about x86-64.
        if (!Constrains(MachineLowByte, offset, mask)) return false;

        foreach (var (position, value) in X86_64Header)
        {
            var index = position - offset;
            if (index < 0 || index >= magic.Length) continue;
            if ((value & mask[index]) != (magic[index] & mask[index])) return false;
        }

        return true;
    }

    private static bool Constrains(int position, int offset, byte[] mask)
    {
        var index = position - offset;
        return index >= 0 && index < mask.Length && mask[index] != 0;
    }

    private static byte[]? ParseHex(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length % 2 != 0) return null;
        try
        {
            return Convert.FromHexString(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
