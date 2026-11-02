namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// Tallies the pixel layouts of DDS files by reading only their headers, so a list's sources can be checked for
/// layouts <see cref="DdsHeader"/> cannot read before an install meets them. Nothing is decoded.
/// </summary>
public sealed class TextureCensus
{
    private readonly SortedDictionary<string, int> _layouts = new(StringComparer.Ordinal);
    private readonly List<(string Name, string Reason)> _unreadable = [];

    /// <summary>Each layout seen, by its DXGI name, with how many files hold it.</summary>
    public IReadOnlyDictionary<string, int> Layouts => _layouts;

    /// <summary>The files whose header could not be read, with the reader's message.</summary>
    public IReadOnlyList<(string Name, string Reason)> Unreadable => _unreadable;

    /// <summary>Records a file that could not be read at all, such as an archive that would not extract.</summary>
    public void AddUnreadable(string name, string reason) => _unreadable.Add((name, reason));

    /// <summary>Reads the header of <paramref name="dds"/> and counts it, or records <paramref name="name"/> as unreadable.</summary>
    public void Add(string name, ReadOnlySpan<byte> dds)
    {
        try
        {
            var header = DdsHeader.Read(dds);
            var layout = header.Format + (header.IsSrgb ? "_SRGB" : "") + (header.FaceCount == 6 ? " (cube map)" : "");
            _layouts[layout] = _layouts.GetValueOrDefault(layout) + 1;
        }
        catch (Exception ex) when (ex is InvalidDataException or UnsupportedTextureFormatException)
        {
            _unreadable.Add((name, ex.Message));
        }
    }
}
