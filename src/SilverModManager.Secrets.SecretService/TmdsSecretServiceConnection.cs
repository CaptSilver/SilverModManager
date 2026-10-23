using System.Text;
using R3;
using Tmds.DBus.Protocol;

namespace NexusMods.Secrets.SecretService;

/// <summary>
/// <see cref="ISecretServiceConnection"/> over Tmds.DBus.Protocol, one method call per message.
/// </summary>
/// <remarks>
/// <see cref="MessageWriter"/> is a struct: a writer handed to a helper by value writes into a copy and the
/// bus drops the connection without a word, so every body writer takes it by <c>ref</c>.
/// The session is the plain one. Anything that can read the user's bus socket can already ask the keyring
/// for the same secrets, so encrypting them in transit would add code and no protection.
/// </remarks>
internal sealed class TmdsSecretServiceConnection : ISecretServiceConnection, IDisposable
{
    private const string Service = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string BusName = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";
    private const string NoObject = "/";

    private delegate void BodyWriter(ref MessageWriter writer);

    private Connection? _connection;
    private string? _session;

    public async Task ConnectAsync(string address, CancellationToken cancellationToken)
    {
        // A reconnect after the bus went away starts from a fresh connection and a fresh session.
        _connection?.Dispose();
        _session = null;

        var connection = new Connection(new ClientConnectionOptions(address) { AutoConnect = false });
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        _connection = connection;
    }

    public async Task<string?> ReadDefaultCollectionAsync(CancellationToken cancellationToken)
    {
        var path = await CallAsync(Service, ServicePath, ServiceInterface, "ReadAlias", "s",
            (ref MessageWriter w) => w.WriteString("default"),
            (m, _) => m.GetBodyReader().ReadObjectPath().ToString(), cancellationToken);
        return path == NoObject ? null : path;
    }

    public Task<bool> IsLockedAsync(string collection, CancellationToken cancellationToken) =>
        CallAsync(Service, collection, PropertiesInterface, "Get", "ss",
            (ref MessageWriter w) =>
            {
                w.WriteString(CollectionInterface);
                w.WriteString("Locked");
            },
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadSignature("b");
                return reader.ReadBool();
            }, cancellationToken);

    public Task<SearchResult> SearchAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken cancellationToken) =>
        CallAsync(Service, ServicePath, ServiceInterface, "SearchItems", "a{ss}",
            (ref MessageWriter w) => WriteAttributes(ref w, attributes),
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                var unlocked = ToStrings(reader.ReadArrayOfObjectPath());
                var locked = ToStrings(reader.ReadArrayOfObjectPath());
                return new SearchResult(unlocked, locked);
            }, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> GetAttributesAsync(string item, CancellationToken cancellationToken) =>
        CallAsync<IReadOnlyDictionary<string, string>>(Service, item, PropertiesInterface, "Get", "ss",
            (ref MessageWriter w) =>
            {
                w.WriteString(ItemInterface);
                w.WriteString("Attributes");
            },
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadSignature("a{ss}");
                var attributes = new Dictionary<string, string>();
                var end = reader.ReadDictionaryStart();
                while (reader.HasNext(end))
                {
                    reader.AlignStruct();
                    attributes[reader.ReadString()] = reader.ReadString();
                }
                return attributes;
            }, cancellationToken);

    public async Task<string> GetSecretAsync(string item, CancellationToken cancellationToken)
    {
        var session = await SessionAsync(cancellationToken);
        var secrets = await WithSessionAsync(CallAsync(Service, ServicePath, ServiceInterface, "GetSecrets", "aoo",
            (ref MessageWriter w) =>
            {
                w.WriteArray(new[] { new ObjectPath(item) });
                w.WriteObjectPath(session);
            },
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                var values = new List<string>();
                var end = reader.ReadDictionaryStart();
                while (reader.HasNext(end))
                {
                    reader.AlignStruct(); // dictionary entry
                    reader.ReadObjectPath();
                    reader.AlignStruct(); // the (oayays) secret
                    reader.ReadObjectPath();
                    reader.ReadArrayOfByte();
                    values.Add(Encoding.UTF8.GetString(reader.ReadArrayOfByte()));
                    reader.ReadString();
                }
                return values;
            }, cancellationToken));

        // A locked item comes back as an empty reply rather than an error.
        return secrets.Count == 1
            ? secrets[0]
            : throw new SecretServiceException(SecretServiceFailure.Other, $"GetSecrets returned {secrets.Count} secrets for {item}");
    }

    public async Task<CreateItemResult> CreateItemAsync(
        string collection,
        string label,
        IReadOnlyDictionary<string, string> attributes,
        string value,
        bool replace,
        CancellationToken cancellationToken)
    {
        var session = await SessionAsync(cancellationToken);
        var (item, prompt) = await WithSessionAsync(CallAsync(Service, collection, CollectionInterface, "CreateItem", "a{sv}(oayays)b",
            (ref MessageWriter w) =>
            {
                var properties = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart();
                w.WriteString($"{ItemInterface}.Label");
                w.WriteVariantString(label);
                w.WriteDictionaryEntryStart();
                w.WriteString($"{ItemInterface}.Attributes");
                w.WriteSignature("a{ss}");
                WriteAttributes(ref w, attributes);
                w.WriteDictionaryEnd(properties);

                w.WriteStructureStart();
                w.WriteObjectPath(session);
                w.WriteArray(Array.Empty<byte>());
                w.WriteArray(Encoding.UTF8.GetBytes(value));
                w.WriteString("text/plain");
                w.WriteBool(replace);
            },
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (Item: reader.ReadObjectPath().ToString(), Prompt: reader.ReadObjectPath().ToString());
            }, cancellationToken));

        return new CreateItemResult(item == NoObject ? null : item, prompt == NoObject ? null : prompt);
    }

    public async Task<string?> DeleteAsync(string item, CancellationToken cancellationToken)
    {
        var prompt = await CallAsync(Service, item, ItemInterface, "Delete", null,
            (ref MessageWriter _) => { },
            (m, _) => m.GetBodyReader().ReadObjectPath().ToString(), cancellationToken);
        return prompt == NoObject ? null : prompt;
    }

    public async Task<UnlockResult> UnlockAsync(IReadOnlyList<string> objects, CancellationToken cancellationToken)
    {
        var (unlocked, prompt) = await CallAsync(Service, ServicePath, ServiceInterface, "Unlock", "ao",
            (ref MessageWriter w) => w.WriteArray(objects.Select(o => new ObjectPath(o)).ToArray()),
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                return (Unlocked: ToStrings(reader.ReadArrayOfObjectPath()), Prompt: reader.ReadObjectPath().ToString());
            }, cancellationToken);

        return new UnlockResult(unlocked, prompt == NoObject ? null : prompt);
    }

    public async Task<bool> PromptAsync(string prompt, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe before asking for the prompt, or a fast daemon's Completed is missed. A daemon that
        // never answers (ksecretd without its prompter) leaves this open until the token fires.
        var rule = new MatchRule { Type = MessageType.Signal, Interface = PromptInterface, Member = "Completed", Path = prompt };
        using var subscription = await RequireConnection().AddMatchAsync<bool>(rule,
            (m, _) => m.GetBodyReader().ReadBool(),
            (exception, dismissed, _, _) =>
            {
                if (exception is not null) completed.TrySetException(Translate(exception));
                else completed.TrySetResult(dismissed);
            },
            null, null, false, ObserverFlags.None).AsTask().WaitAsync(cancellationToken);

        await CallAsync<int>(Service, prompt, PromptInterface, "Prompt", "s",
            (ref MessageWriter w) => w.WriteString(""), (_, _) => 0, cancellationToken);

        return await completed.Task.WaitAsync(cancellationToken);
    }

    public async Task StartServiceByNameAsync(string name, CancellationToken cancellationToken) =>
        await CallAsync(BusName, BusPath, BusName, "StartServiceByName", "su",
            (ref MessageWriter w) =>
            {
                w.WriteString(name);
                w.WriteUInt32(0);
            },
            (m, _) => m.GetBodyReader().ReadUInt32(), cancellationToken);

    /// <remarks>
    /// The stream ends quietly if the signal subscription fails or the bus closes; the store re-reads
    /// <c>Locked</c> whenever it describes or unlocks, so a missed edge costs freshness, not correctness.
    /// </remarks>
    public Observable<bool> LockedChanged(string collection) => Observable.Create<bool>(async (observer, cancellationToken) =>
    {
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = Service,
            Interface = PropertiesInterface,
            Member = "PropertiesChanged",
            Path = collection,
        };

        try
        {
            using var subscription = await RequireConnection().AddMatchAsync<bool?>(rule,
                (m, _) =>
                {
                    var reader = m.GetBodyReader();
                    if (reader.ReadString() != CollectionInterface) return null;
                    var changed = reader.ReadDictionaryOfStringToVariantValue();
                    return changed.TryGetValue("Locked", out var locked) ? locked.GetBool() : null;
                },
                (exception, locked, _, _) =>
                {
                    if (exception is not null) observer.OnCompleted();
                    else if (locked is { } value) observer.OnNext(value);
                },
                null, null, false, ObserverFlags.None).AsTask().WaitAsync(cancellationToken);

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The subscriber went away.
        }
        catch (Exception e) when (e is DBusException or ConnectException or ObjectDisposedException)
        {
            observer.OnCompleted();
        }
    });

    public void Dispose() => _connection?.Dispose();

    private Connection RequireConnection() =>
        _connection ?? throw new InvalidOperationException("ConnectAsync has not completed.");

    /// <summary>
    /// The session is opened on first use, not in <see cref="ConnectAsync"/>: opening it is the first call that
    /// needs the keyring, and the store only starts the keyring for a failure it sees after connecting.
    /// Two callers racing here open two sessions, which costs the daemon an object and nothing else.
    /// </summary>
    private async Task<string> SessionAsync(CancellationToken cancellationToken) =>
        _session ??= await CallAsync(Service, ServicePath, ServiceInterface, "OpenSession", "sv",
            (ref MessageWriter w) =>
            {
                w.WriteString("plain");
                w.WriteVariantString("");
            },
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadVariantValue(); // the algorithm output, empty for plain
                return reader.ReadObjectPath().ToString();
            }, cancellationToken);

    /// <summary>
    /// Forgets the session when a call that used it fails: a keyring restarted on the same bus no longer knows it.
    /// </summary>
    private async Task<T> WithSessionAsync<T>(Task<T> call)
    {
        try
        {
            return await call;
        }
        catch (SecretServiceException)
        {
            _session = null;
            throw;
        }
    }

    private async Task<T> CallAsync<T>(
        string destination,
        string path,
        string @interface,
        string member,
        string? signature,
        BodyWriter body,
        MessageValueReader<T> reader,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        var writer = connection.GetMessageWriter();
        Task<T> call;
        try
        {
            writer.WriteMethodCallHeader(destination: destination, path: path, @interface: @interface, member: member, signature: signature);
            body(ref writer);
            call = connection.CallMethodAsync(writer.CreateMessage(), reader, null);
        }
        finally
        {
            writer.Dispose();
        }

        try
        {
            return await call.WaitAsync(cancellationToken);
        }
        catch (Exception e) when (e is DBusException or DisconnectedException or ObjectDisposedException)
        {
            var translated = Translate(e);
            // The daemon that owned the session is gone, so whichever one answers next will not know it.
            if (translated.Failure is SecretServiceFailure.ServiceUnknown or SecretServiceFailure.NoReply) _session = null;
            throw translated;
        }
    }

    internal static SecretServiceException Translate(Exception e)
    {
        var name = (e as DBusException)?.ErrorName ?? "";
        var failure = name switch
        {
            _ when name.EndsWith("ServiceUnknown", StringComparison.Ordinal) => SecretServiceFailure.ServiceUnknown,
            _ when name.EndsWith("NoReply", StringComparison.Ordinal) => SecretServiceFailure.NoReply,
            _ when name.EndsWith("Disconnected", StringComparison.Ordinal) => SecretServiceFailure.Disconnected,
            _ when name.EndsWith("IsLocked", StringComparison.Ordinal) => SecretServiceFailure.Locked,
            _ when e is DisconnectedException or ObjectDisposedException => SecretServiceFailure.Disconnected,
            _ => SecretServiceFailure.Other,
        };
        return new SecretServiceException(failure, e.Message, e);
    }

    private static void WriteAttributes(ref MessageWriter writer, IReadOnlyDictionary<string, string> attributes)
    {
        var start = writer.WriteDictionaryStart();
        foreach (var (name, value) in attributes)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(name);
            writer.WriteString(value);
        }
        writer.WriteDictionaryEnd(start);
    }

    private static string[] ToStrings(ObjectPath[] paths) => paths.Select(p => p.ToString()).ToArray();
}
