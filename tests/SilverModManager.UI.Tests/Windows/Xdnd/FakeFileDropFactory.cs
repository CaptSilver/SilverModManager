using Avalonia.Input;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>
/// An IFileDropFactory that answers at once with a fresh DataObject, unless a test calls <see cref="Hold"/> to keep
/// the next request pending (then <see cref="Release"/> completes it) or sets <see cref="Failure"/>.
/// </summary>
public sealed class FakeFileDropFactory : IFileDropFactory
{
    private TaskCompletionSource? _gate;

    public List<IReadOnlyList<string>> Requests { get; } = [];
    public List<DataObject> Created { get; } = [];

    /// <summary>Paths reported as unopenable in every result.</summary>
    public IReadOnlyList<string> Missing { get; set; } = [];

    public Exception? Failure { get; set; }

    public void Hold() => _gate = new TaskCompletionSource();

    public void Release() => _gate!.SetResult();

    public async Task<FileDrop> CreateAsync(IReadOnlyList<string> paths)
    {
        Requests.Add(paths);
        if (_gate is { } gate) await gate.Task;
        if (Failure is { } failure) throw failure;
        var data = new DataObject();
        Created.Add(data);
        return new FileDrop(data, Missing);
    }
}
