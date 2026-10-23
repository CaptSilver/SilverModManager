using FluentAssertions;
using NexusMods.Abstractions.Secrets;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.Secrets.Overlay;

namespace NexusMods.Networking.NexusWebApi.Tests;

public class LoginVerbsTests : IDisposable
{
    private readonly InMemorySecretStore _store = new();
    private readonly List<NexusCredentialCache> _caches = [];
    private readonly RecordingRenderer _renderer = new();

    public void Dispose()
    {
        foreach (var cache in _caches) cache.Dispose();
    }

    private sealed class RecordingRenderer : IRenderer
    {
        public List<string> Lines { get; } = [];

        public ValueTask RenderAsync(IRenderable renderable)
        {
            if (renderable is not Text text) return ValueTask.CompletedTask;

            var line = text.Template;
            for (var i = 0; i < text.Arguments.Length; i++) line = line.Replace($"{{{i}}}", text.Arguments[i]);
            Lines.AddRange(line.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync() => ValueTask.CompletedTask;
    }

    private ApiKeyMessageFactory Factory(ISecretStore store)
    {
        var cache = AuthTestSupport.CreateCache(store);
        _caches.Add(cache);
        return new ApiKeyMessageFactory(cache);
    }

    [Fact]
    public async Task ReadsTheKeyFromStdinAndStoresIt()
    {
        var exit = await LoginVerbs.StoreApiKeyAsync(Factory(_store), _renderer, new StringReader("  the-key \n"), CancellationToken.None);

        exit.Should().Be(0);
        (await AuthTestSupport.ReadKey(_store)).Should().Be("the-key");
    }

    [Fact]
    public async Task ReadsOnlyTheFirstLine()
    {
        var exit = await LoginVerbs.StoreApiKeyAsync(Factory(_store), _renderer, new StringReader("first\nsecond\n"), CancellationToken.None);

        exit.Should().Be(0);
        (await AuthTestSupport.ReadKey(_store)).Should().Be("first");
    }

    [Fact]
    public async Task AnEmptyStdinStoresNothingAndFails()
    {
        var exit = await LoginVerbs.StoreApiKeyAsync(Factory(_store), _renderer, new StringReader(""), CancellationToken.None);

        exit.Should().Be(1);
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
        _renderer.Lines.Should().ContainSingle().Which.Should().Contain("stdin");
    }

    [Fact]
    public async Task APinnedKeyPrintsItsSourceStoresNothingAndFails()
    {
        var overlay = new OverlaySecretStore(_store, new FakeEnvironment((NexusSecretKeys.ApiKeyEnvironmentVariable, "pinned")), null,
            new Dictionary<SecretKey, string> { [NexusSecretKeys.ApiKey] = NexusSecretKeys.ApiKeyEnvironmentVariable });

        var exit = await LoginVerbs.StoreApiKeyAsync(Factory(overlay), _renderer, new StringReader("typed\n"), CancellationToken.None);

        exit.Should().Be(1);
        _renderer.Lines.Should().ContainSingle().Which.Should().Be("The API key is set by environment variable NEXUS_API_KEY; unset it to store a different one");
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
    }

    [Fact]
    public async Task AnUnavailableStorePrintsTheReasonAndFails()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "the wallet is locked");

        var exit = await LoginVerbs.StoreApiKeyAsync(Factory(_store), _renderer, new StringReader("typed\n"), CancellationToken.None);

        exit.Should().Be(1);
        _renderer.Lines.Should().ContainSingle().Which.Should().Contain("the wallet is locked");
        _renderer.Lines.Should().NotContain(l => l.Contains("typed"));
    }

    [Fact]
    public void TheVerbHasNoApiKeyOption()
    {
        // The key must never travel on the command line, so there is no option to carry it.
        var verb = typeof(LoginVerbs).GetMethod("SetApiKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        verb.GetParameters().SelectMany(p => p.GetCustomAttributes(false)).OfType<NexusMods.Sdk.ProxyConsole.OptionAttribute>().Should().BeEmpty();
    }
}
