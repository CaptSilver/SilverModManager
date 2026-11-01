using System.Text.Json;

namespace SilverModManager.App.UI.X11.TestHost;

/// <summary>Writes one JSON line per report to the test process, which reads them from stdout.</summary>
public sealed class HostReport(TextWriter output)
{
    private readonly object _gate = new();

    public void Ready(nint xid, int rootX, int rootY) =>
        Write(new { kind = "ready", xid = (long)xid, x = rootX, y = rootY });

    public void Drag(string kind, IReadOnlyList<string> paths) =>
        Write(new { kind, paths });

    private void Write<T>(T line)
    {
        lock (_gate)
        {
            output.WriteLine(JsonSerializer.Serialize(line));
            output.Flush();
        }
    }
}
