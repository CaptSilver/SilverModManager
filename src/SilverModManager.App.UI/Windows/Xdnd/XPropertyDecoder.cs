namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>
/// Turns the memory XGetWindowProperty returns into an <see cref="XProperty"/>. Xlib hands back format 8 as
/// <c>char</c>, format 16 as <c>short</c> and format 32 as <c>long</c>, so a format-32 item is pointer-sized on 64-bit Linux.
/// </summary>
public static class XPropertyDecoder
{
    /// <returns>null when the server reported no such property (format 0).</returns>
    public static unsafe XProperty? Decode(nint type, int format, nint items, nint data)
    {
        if (format == 0) return null;
        var itemSize = format switch
        {
            8 => 1,
            16 => sizeof(short),
            32 => sizeof(nint),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "X properties are format 8, 16 or 32"),
        };

        var bytes = new byte[(int)items * itemSize];
        if (bytes.Length > 0) new ReadOnlySpan<byte>((void*)data, bytes.Length).CopyTo(bytes);
        return new XProperty(type, format, bytes);
    }
}
