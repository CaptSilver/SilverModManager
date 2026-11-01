using Microsoft.Extensions.DependencyInjection;
using SilverModManager.CLI.Types;
using SilverModManager.Sdk;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.CLI;

/// <summary>
/// CLI verbs for the protocols
/// </summary>
public static class ProtocolVerbs
{
    /// <summary>
    /// Adds the protocol verbs to the <see cref="IServiceCollection"/>
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddProtocolVerbs(this IServiceCollection services) =>
        services.AddVerb(() => AssociateNxm)
            .AddVerb(() => ProtocolInvoke);


    [Verb("associate-nxm", "Associate the nxm:// protocol with this application")]
    private static async Task<int> AssociateNxm([Injected] IOSInterop osInterop, [Injected] ProtocolNotices notices, [Injected] CancellationToken token)
    {
        await Associate("nxm", osInterop, notices, token);
        return 0;
    }

    /// <summary>
    /// Takes <paramref name="scheme"/> from whichever app holds it. A verb runs without <see cref="ProtocolRegistration"/>,
    /// so it clears the remembered holder itself.
    /// </summary>
    public static async Task<UriSchemeRegistration> Associate(string scheme, IOSInterop osInterop, ProtocolNotices notices, CancellationToken token)
    {
        var result = await osInterop.RegisterUriSchemeHandler(scheme, UriSchemeClaim.Take, cancellationToken: token);
        if (result.Registered)
            notices.Forget(scheme);
        return result;
    }
    
    [Verb("protocol-invoke", "Handle a URL with custom protocol")]
    private static async Task<int> ProtocolInvoke([Injected] IRenderer renderer,
        [Option("u", "url", "The URL to handle")] Uri uri,
        [Injected] IEnumerable<IIpcProtocolHandler> handlers,
        [Injected] CancellationToken token)
    {
        var handler = handlers.FirstOrDefault(iter => iter.Protocol == uri.Scheme);
        if (handler == null)
            throw new Exception($"Unsupported protocol \"{uri.Scheme}\"");

        await handler.Handle(uri.ToString(), token);

        return 0;
    }

}
