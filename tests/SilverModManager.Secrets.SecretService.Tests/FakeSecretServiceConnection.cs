using SilverModManager.Secrets.SecretService;
using R3;

namespace SilverModManager.Secrets.SecretService.Tests;

/// <summary>
/// A scripted <see cref="ISecretServiceConnection"/>. Each method has a queue of steps that are consumed in order,
/// and a sticky step that answers once the queue is empty. A method nobody scripted throws, so a test never
/// passes because the store made a call it did not expect.
/// </summary>
internal sealed class FakeSecretServiceConnection : ISecretServiceConnection
{
    public const string DefaultCollection = "/org/freedesktop/secrets/collection/login";
    public const string Item1 = "/org/freedesktop/secrets/collection/login/1";

    public sealed record CreateItemCall(
        string Collection, string Label, IReadOnlyDictionary<string, string> Attributes, string Value, bool Replace);

    public sealed class Script<T>(string name)
    {
        private readonly Queue<Func<CancellationToken, Task<T>>> _queue = new();
        private Func<CancellationToken, Task<T>>? _sticky;

        public Script<T> Then(T value) => Then(_ => Task.FromResult(value));
        public Script<T> Then(Func<CancellationToken, Task<T>> step) { _queue.Enqueue(step); return this; }
        public Script<T> ThenThrow(Exception exception) => Then(_ => Task.FromException<T>(exception));
        public Script<T> ThenHang() => Then(async ct => { await Task.Delay(Timeout.Infinite, ct); return default!; });

        public Script<T> Always(T value) => Always(_ => Task.FromResult(value));
        public Script<T> Always(Func<CancellationToken, Task<T>> step) { _sticky = step; return this; }
        public Script<T> AlwaysThrow(Exception exception) => Always(_ => Task.FromException<T>(exception));

        public Task<T> Next(CancellationToken ct)
        {
            if (_queue.TryDequeue(out var step)) return step(ct);
            if (_sticky is not null) return _sticky(ct);
            throw new InvalidOperationException($"The fake connection was asked for {name} but no response is scripted.");
        }
    }

    private readonly object _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<CreateItemCall> _createItemCalls = [];

    public Script<bool> Connect { get; } = new("Connect");
    public Script<string?> ReadDefaultCollection { get; } = new("ReadDefaultCollection");
    public Script<bool> IsLocked { get; } = new("IsLocked");
    public Script<SearchResult> Search { get; } = new("Search");
    public Script<IReadOnlyDictionary<string, string>> GetAttributes { get; } = new("GetAttributes");
    public Script<string> GetSecret { get; } = new("GetSecret");
    public Script<CreateItemResult> CreateItem { get; } = new("CreateItem");
    public Script<string?> Delete { get; } = new("Delete");
    public Script<UnlockResult> Unlock { get; } = new("Unlock");
    public Script<bool> Prompt { get; } = new("Prompt");
    public Script<bool> StartServiceByName { get; } = new("StartServiceByName");

    /// <summary>Push a locked state as the bus signal would.</summary>
    public Subject<bool> LockedChangedSubject { get; } = new();

    public IReadOnlyList<string> Calls { get { lock (_gate) return _calls.ToArray(); } }
    public IReadOnlyList<CreateItemCall> CreateItemCalls { get { lock (_gate) return _createItemCalls.ToArray(); } }
    public IReadOnlyList<IReadOnlyDictionary<string, string>> SearchAttributes { get { lock (_gate) return _searchAttributes.ToArray(); } }
    private readonly List<IReadOnlyDictionary<string, string>> _searchAttributes = [];

    public int CountCalls(string name) => Calls.Count(c => c == name);

    private void Record(string call) { lock (_gate) _calls.Add(call); }

    public async Task ConnectAsync(string address, CancellationToken cancellationToken)
    {
        Record("Connect");
        lock (_gate) ConnectedAddresses.Add(address);
        await Connect.Next(cancellationToken);
    }

    public List<string> ConnectedAddresses { get; } = [];

    public Task<string?> ReadDefaultCollectionAsync(CancellationToken cancellationToken)
    {
        Record("ReadDefaultCollection");
        return ReadDefaultCollection.Next(cancellationToken);
    }

    public Task<bool> IsLockedAsync(string collection, CancellationToken cancellationToken)
    {
        Record("IsLocked");
        return IsLocked.Next(cancellationToken);
    }

    public Task<SearchResult> SearchAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken cancellationToken)
    {
        Record("Search");
        lock (_gate) _searchAttributes.Add(attributes);
        return Search.Next(cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, string>> GetAttributesAsync(string item, CancellationToken cancellationToken)
    {
        Record("GetAttributes");
        return GetAttributes.Next(cancellationToken);
    }

    public Task<string> GetSecretAsync(string item, CancellationToken cancellationToken)
    {
        Record("GetSecret");
        return GetSecret.Next(cancellationToken);
    }

    public Task<CreateItemResult> CreateItemAsync(string collection, string label, IReadOnlyDictionary<string, string> attributes, string value, bool replace, CancellationToken cancellationToken)
    {
        Record("CreateItem");
        lock (_gate) _createItemCalls.Add(new CreateItemCall(collection, label, attributes, value, replace));
        return CreateItem.Next(cancellationToken);
    }

    public Task<string?> DeleteAsync(string item, CancellationToken cancellationToken)
    {
        Record("Delete");
        return Delete.Next(cancellationToken);
    }

    public Task<UnlockResult> UnlockAsync(IReadOnlyList<string> objects, CancellationToken cancellationToken)
    {
        Record("Unlock");
        return Unlock.Next(cancellationToken);
    }

    public Task<bool> PromptAsync(string prompt, CancellationToken cancellationToken)
    {
        Record("Prompt");
        return Prompt.Next(cancellationToken);
    }

    public Task StartServiceByNameAsync(string name, CancellationToken cancellationToken)
    {
        Record($"StartServiceByName({name})");
        return StartServiceByName.Next(cancellationToken);
    }

    public Observable<bool> LockedChanged(string collection)
    {
        Record("LockedChanged");
        return LockedChangedSubject;
    }

    /// <summary>The fake accepts a default collection, an unlocked collection and nothing else, until a test says otherwise.</summary>
    public FakeSecretServiceConnection WithUnlockedDefaultCollection()
    {
        Connect.Always(true);
        ReadDefaultCollection.Always(DefaultCollection);
        IsLocked.Always(false);
        return this;
    }
}
