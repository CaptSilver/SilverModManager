using System.Text.Json;

namespace SilverModManager.App.UI.X11.Tests;

/// <summary>One JSON line the test host wrote to stdout.</summary>
/// <param name="Kind"><c>ready</c>, <c>DragEnter</c>, <c>DragOver</c>, <c>DragLeave</c> or <c>Drop</c>.</param>
/// <param name="Xid">The window's XID; <c>ready</c> only.</param>
/// <param name="X">The drop border's centre in root coordinates; <c>ready</c> only.</param>
/// <param name="Y">As <paramref name="X"/>.</param>
/// <param name="Paths">The local paths the drag event carried; drag events only.</param>
internal sealed record HostReport(string Kind, long Xid = 0, int X = 0, int Y = 0, string[]? Paths = null)
{
    public static HostReport? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<HostReport>(line, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
