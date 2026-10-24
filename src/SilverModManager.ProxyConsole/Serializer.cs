using System.Buffers;
using System.Buffers.Binary;
using JetBrains.Annotations;
using MemoryPack;
using MemoryPack.Formatters;
using SilverModManager.ProxyConsole.Exceptions;
using SilverModManager.ProxyConsole.Messages;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.ProxyConsole;


/// <summary>
/// Serializes and deserializes messages to and from the server, and manages the underlying
/// duplex stream.
/// </summary>
public class Serializer
{
    private const int HeaderSize = sizeof(int);

    private readonly Stream _stream;
    private readonly MemoryPool<byte> _memoryPool;

    /// <summary>
    /// Primary constructor, takes a duplex capable stream
    /// </summary>
    /// <param name="duplexStream"></param>
    /// <param name="renderableDefinitions"></param>
    public Serializer(Stream duplexStream, IEnumerable<IRenderableDefinition> renderableDefinitions)
    {
        _stream = duplexStream;
        _memoryPool = MemoryPool<byte>.Shared;

        if (!MemoryPackFormatterProvider.IsRegistered<IRenderable>())
        {
            // In the future we *could* redo this to use the guid as a tag, but since all this data is
            // ephemeral, it doesn't matter for now.
            var listDefs = renderableDefinitions
                .OrderBy(l => l.Id)
                .Select((def, idx) => ((ushort)idx, def.RenderableType));

            MemoryPackFormatterProvider.Register(new DynamicUnionFormatter<IRenderable>(listDefs.ToArray()));
        }
    }

    /// <summary>
    /// Sends the given message to the server and waits for an acknowledgement.
    /// </summary>
    /// <param name="msg"></param>
    /// <typeparam name="TMessage"></typeparam>
    [PublicAPI]
    public async Task SendAndAckAsync<TMessage>(TMessage msg)
    where TMessage : IMessage
    {
        await SendAsync(msg);
        await ReceiveExactlyAsync<Acknowledge>();
    }

    /// <summary>
    /// Sends the given message to the server and waits for a response of the given type.
    /// </summary>
    /// <param name="msg"></param>
    /// <typeparam name="TResponse"></typeparam>
    /// <typeparam name="TRequest"></typeparam>
    /// <returns></returns>
    public async Task<TResponse> SendAndReceiveAsync<TResponse, TRequest>(TRequest msg)
        where TResponse : class, IMessage
        where TRequest : IMessage
    {
        await SendAsync(msg);
        return await ReceiveExactlyAsync<TResponse>();
    }

    /// <summary>
    /// Sends the given message to the server and does not wait for a response.
    /// </summary>
    /// <param name="msg"></param>
    /// <typeparam name="TMessage"></typeparam>
    public async Task SendAsync<TMessage>(TMessage msg) where TMessage : IMessage
    {
        var serialized = MemoryPackSerializer.Serialize<IMessage>(msg);

        // Length and body go out in one asynchronous write: the old BinaryWriter wrote the length synchronously on the
        // caller's thread, and with Nagle off that length left as a packet of its own.
        var frame = new byte[HeaderSize + serialized.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, serialized.Length);
        serialized.CopyTo(frame, HeaderSize);
        await _stream.WriteAsync(frame);
    }

    /// <summary>
    /// Sends an acknowledgement to the server.
    /// </summary>
    [PublicAPI]
    public Task AcknowledgeAsync()
    {
        return SendAsync(new Acknowledge());
    }

    /// <summary>
    /// Receives a message of the given type from the server, if the message is not of the given type an
    /// exception is thrown.
    /// </summary>
    /// <typeparam name="TMessage"></typeparam>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private async Task<TMessage> ReceiveExactlyAsync<TMessage>() where TMessage : class, IMessage
    {
        var deserialized = await ReceiveAsync();

        if (deserialized is not TMessage)
            UnexpectedMessageException.Throw(typeof(TMessage), deserialized?.GetType() ?? typeof(object));
        return (TMessage)deserialized!;
    }

    /// <summary>
    /// Receives the next message from the server.
    /// </summary>
    /// <returns></returns>
    public async Task<IMessage?> ReceiveAsync()
    {
        // The header is read asynchronously too: a blocking read here holds the caller's thread until the peer
        // speaks, which for a forwarded launch is until the game ends.
        var header = new byte[HeaderSize];
        await _stream.ReadExactlyAsync(header);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);

        using var buffer = _memoryPool.Rent((int)size);
        var sized = buffer.Memory[..(int)size];
        await _stream.ReadExactlyAsync(sized);
        var deserialized = MemoryPackSerializer.Deserialize<IMessage>(sized.Span);
        return deserialized;
    }
}
