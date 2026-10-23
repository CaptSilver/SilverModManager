using System.Text;

namespace SilverModManager.Games.Bethesda.LoadOrder.Tests;

/// <summary>Everything a synthetic plugin header can carry. Null fields are left out of the file.</summary>
internal sealed record PluginSpec
{
    public uint RecordFlags { get; init; }
    public IReadOnlyList<string> Masters { get; init; } = [];
    public float? HeaderVersion { get; init; } = 1.0f;
    public uint? RecordCount { get; init; } = 0;
    public string? Description { get; init; }
    public string? Author { get; init; }
}

/// <summary>
/// Writes minimal plugins holding just the header record, in the byte layout the three
/// formats use (record header 16 bytes for TES3, 20 for Oblivion, 24 for the rest).
/// </summary>
internal static class PluginBytes
{
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)
        ?? throw new InvalidOperationException("The Windows-1252 code page is not available");

    public static byte[] Write(PluginFormat format, PluginSpec spec) => format switch
    {
        PluginFormat.Tes3 => Tes3(spec),
        PluginFormat.Tes4Oblivion => Tes4(spec, oblivion: true),
        PluginFormat.Tes4 => Tes4(spec, oblivion: false),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static byte[] Tes3(PluginSpec spec)
    {
        // The TES3 flag word that marks a master lives in HEDR, not in the record header.
        var hedr = new byte[300];
        BitConverter.GetBytes(spec.HeaderVersion ?? 0f).CopyTo(hedr, 0);
        BitConverter.GetBytes(spec.RecordFlags).CopyTo(hedr, 4);
        PutString(hedr, 8, 32, spec.Author);
        PutString(hedr, 40, 256, spec.Description);
        BitConverter.GetBytes(spec.RecordCount ?? 0u).CopyTo(hedr, 296);

        var body = new MemoryStream();
        if (spec.HeaderVersion is not null || spec.RecordCount is not null) Subrecord(body, "HEDR", hedr, wide: true);
        foreach (var master in spec.Masters)
        {
            Subrecord(body, "MAST", CString(master), wide: true);
            Subrecord(body, "DATA", new byte[8], wide: true);
        }

        var file = new MemoryStream();
        file.Write("TES3"u8);
        file.Write(BitConverter.GetBytes((uint)body.Length));
        file.Write(new byte[4]);
        file.Write(BitConverter.GetBytes(0u));
        body.WriteTo(file);
        return file.ToArray();
    }

    public static byte[] Tes4(PluginSpec spec, bool oblivion)
    {
        var body = new MemoryStream();
        if (spec.HeaderVersion is not null || spec.RecordCount is not null)
        {
            var hedr = new byte[12];
            BitConverter.GetBytes(spec.HeaderVersion ?? 0f).CopyTo(hedr, 0);
            BitConverter.GetBytes(spec.RecordCount ?? 0u).CopyTo(hedr, 4);
            Subrecord(body, "HEDR", hedr, wide: false);
        }
        if (spec.Author is not null) Subrecord(body, "CNAM", CString(spec.Author), wide: false);
        if (spec.Description is not null) Subrecord(body, "SNAM", CString(spec.Description), wide: false);
        foreach (var master in spec.Masters)
        {
            Subrecord(body, "MAST", CString(master), wide: false);
            Subrecord(body, "DATA", new byte[8], wide: false);
        }

        var file = new MemoryStream();
        file.Write("TES4"u8);
        file.Write(BitConverter.GetBytes((uint)body.Length));
        file.Write(BitConverter.GetBytes(spec.RecordFlags));
        file.Write(BitConverter.GetBytes(0u));
        file.Write(new byte[oblivion ? 4 : 8]);
        body.WriteTo(file);
        return file.ToArray();
    }

    /// <summary>A TES4 header whose SNAM is larger than a u16 length can hold, preceded by an XXXX size subrecord.</summary>
    public static byte[] Tes4WithPresizedDescription(string description, uint declaredSize)
    {
        var body = new MemoryStream();
        Subrecord(body, "XXXX", BitConverter.GetBytes(declaredSize), wide: false);
        body.Write("SNAM"u8);
        body.Write(BitConverter.GetBytes((ushort)0));
        var data = new byte[declaredSize];
        CString(description).CopyTo(data, 0);
        body.Write(data);

        var file = new MemoryStream();
        file.Write("TES4"u8);
        file.Write(BitConverter.GetBytes((uint)body.Length));
        file.Write(new byte[16]);
        body.WriteTo(file);
        return file.ToArray();
    }

    public static byte[] RecordOfType(string type)
    {
        var file = new MemoryStream();
        file.Write(Encoding.ASCII.GetBytes(type));
        file.Write(new byte[20]);
        return file.ToArray();
    }

    private static void Subrecord(Stream to, string type, byte[] data, bool wide)
    {
        to.Write(Encoding.ASCII.GetBytes(type));
        if (wide) to.Write(BitConverter.GetBytes((uint)data.Length));
        else to.Write(BitConverter.GetBytes(checked((ushort)data.Length)));
        to.Write(data);
    }

    private static byte[] CString(string value) => [.. Windows1252.GetBytes(value), 0];

    private static void PutString(byte[] into, int offset, int width, string? value)
    {
        if (value is null) return;
        var bytes = Windows1252.GetBytes(value);
        Array.Copy(bytes, 0, into, offset, Math.Min(bytes.Length, width - 1));
    }
}
