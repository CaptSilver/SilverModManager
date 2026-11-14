using System.Collections.Concurrent;

namespace SilverModManager.Platform.Proton.ToolRunner;

/// <inheritdoc />
public sealed class StartedTools : IStartedTools
{
    private readonly ConcurrentDictionary<int, Entry> _tools = new();

    /// <inheritdoc />
    public IDisposable Register(int pid, string toolName)
    {
        var entry = new Entry(toolName);
        _tools[pid] = entry;
        return new Registration(_tools, pid, entry);
    }

    /// <inheritdoc />
    public string? NameOf(int pid) => _tools.TryGetValue(pid, out var entry) ? entry.Name : null;

    private sealed class Entry(string name)
    {
        public string Name => name;
    }

    // A pid can be reused by a later tool; the earlier tool's late disposal must not remove the newer entry.
    private sealed class Registration(ConcurrentDictionary<int, Entry> tools, int pid, Entry entry) : IDisposable
    {
        public void Dispose() => tools.TryRemove(new KeyValuePair<int, Entry>(pid, entry));
    }
}
