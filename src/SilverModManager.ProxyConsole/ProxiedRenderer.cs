using Microsoft.Extensions.DependencyInjection;
using SilverModManager.ProxyConsole.Exceptions;
using SilverModManager.ProxyConsole.Messages;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.ProxyConsole;

/// <summary>
/// A renderer that proxies render commands to a remote renderer.
/// </summary>
public class ProxiedRenderer : IRenderer
{
    private readonly Serializer _serializer;

    private ProxiedRenderer(Serializer serializer)
    {
        _serializer = serializer;
    }

    /// <summary>
    /// Creates a new <see cref="ProxiedRenderer"/> instance from the given duplex capable stream.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="duplexStream"></param>
    /// <returns></returns>
    public static async Task<(string[]? Arguments, IRenderer Renderer)> Create(IServiceProvider provider, Stream duplexStream)
    {
        var (reply, renderer, _) = await Accept(provider, duplexStream);
        if (reply is not ProgramArgumentsResponse response)
        {
            UnexpectedMessageException.Throw(typeof(ProgramArgumentsResponse), reply?.GetType() ?? typeof(object));
            return default; // Throw never returns
        }

        return (response.Arguments, renderer);
    }

    /// <summary>
    /// Asks the client what it wants and returns its first reply unexamined, with the renderer and serializer over
    /// the same stream. A client that wants to run a verb answers with <see cref="ProgramArgumentsResponse"/>;
    /// anything else is the caller's to interpret.
    /// </summary>
    public static async Task<(IMessage? Reply, IRenderer Renderer, Serializer Serializer)> Accept(IServiceProvider provider, Stream duplexStream)
    {
        var serializer = new Serializer(duplexStream, provider.GetRequiredService<IEnumerable<IRenderableDefinition>>());
        await serializer.SendAsync(new ProgramArgumentsRequest());
        var reply = await serializer.ReceiveAsync();
        return (reply, new ProxiedRenderer(serializer), serializer);
    }

    /// <summary>
    /// Renders the given renderable.
    /// </summary>
    /// <param name="renderable"></param>
    public async ValueTask RenderAsync(IRenderable renderable)
    {
        await _serializer.SendAsync(new Render {Renderable = renderable});
    }

    /// <summary>
    /// Clears the console.
    /// </summary>
    public async ValueTask ClearAsync()
    {
        await _serializer.SendAsync(new Clear());
    }
}
