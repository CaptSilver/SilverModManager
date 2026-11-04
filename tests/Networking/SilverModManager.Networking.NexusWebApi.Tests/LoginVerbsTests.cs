using System.Net;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.DTOs;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.NexusWebApi.Auth;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Secrets.Overlay;

namespace SilverModManager.Networking.NexusWebApi.Tests;

public class LoginVerbsTests : IDisposable
{
    private readonly InMemorySecretStore _store = new();
    private readonly List<NexusCredentialCache> _caches = [];
    private readonly RecordingRenderer _renderer = new();
    private readonly INexusApiClient _client = Substitute.For<INexusApiClient>();

    public LoginVerbsTests()
    {
        _client.Validate(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new Response<ValidateInfo>
        {
            Data = new ValidateInfo { _UserId = 42, Name = "silver" },
            Metadata = new ResponseMetadata(),
            StatusCode = HttpStatusCode.OK,
        }));
    }

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
        var exit = await LoginVerbs.StoreApiKeyAsync(Factory(_store), _client, _renderer, new StringReader("  the-key \n"), CancellationToken.None);

        exit.Should().Be(0);
        (await AuthTestSupport.ReadKey(_store)).Should().Be("the-key");
    }

    [Fact]
    public async Task ReadsOnlyTheFirstLine()
    {
        var exit = await LoginVerbs.StoreApiKeyAsync(Factory(_store), _client, _renderer, new StringReader("first\nsecond\n"), CancellationToken.None);

        exit.Should().Be(0);
        (await AuthTestSupport.ReadKey(_store)).Should().Be("first");
    }

    [Fact]
    public async Task AnEmptyStdinStoresNothingAndFails()
    {
        var exit = await LoginVerbs.StoreApiKeyAsync(Factory(_store), _client, _renderer, new StringReader(""), CancellationToken.None);

        exit.Should().Be(1);
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
        _renderer.Lines.Should().ContainSingle().Which.Should().Contain("stdin");
    }

    [Fact]
    public async Task APinnedKeyPrintsItsSourceStoresNothingAndFails()
    {
        var overlay = new OverlaySecretStore(_store, new FakeEnvironment((NexusSecretKeys.ApiKeyEnvironmentVariable, "pinned")), null,
            new Dictionary<SecretKey, string> { [NexusSecretKeys.ApiKey] = NexusSecretKeys.ApiKeyEnvironmentVariable });

        var exit = await LoginVerbs.StoreApiKeyAsync(Factory(overlay), _client, _renderer, new StringReader("typed\n"), CancellationToken.None);

        exit.Should().Be(1);
        _renderer.Lines.Should().ContainSingle().Which.Should().Be("The API key is set by environment variable NEXUS_API_KEY; unset it to store a different one");
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
    }

    [Fact]
    public async Task AnUnavailableStorePrintsTheReasonAndFails()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "the wallet is locked");

        var exit = await LoginVerbs.StoreApiKeyAsync(Factory(_store), _client, _renderer, new StringReader("typed\n"), CancellationToken.None);

        exit.Should().Be(1);
        _renderer.Lines.Should().ContainSingle().Which.Should().Contain("the wallet is locked");
        _renderer.Lines.Should().NotContain(l => l.Contains("typed"));
    }

    [Fact]
    public async Task TheKeyIsCheckedAgainstNexusBeforeItIsStored()
    {
        var exit = await LoginVerbs.StoreApiKeyAsync(Factory(_store), _client, _renderer, new StringReader("the-key\n"), CancellationToken.None);

        exit.Should().Be(0);
        await _client.Received(1).Validate("the-key", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AKeyNexusRejectsIsRefusedAndNothingIsStored(HttpStatusCode status)
    {
        _client.Validate(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<Response<ValidateInfo>>>(_ => throw new HttpRequestException("rejected", null, status));

        var act = async () => await LoginVerbs.StoreApiKeyAsync(Factory(_store), _client, _renderer, new StringReader("bad\n"), CancellationToken.None);

        await act.Should().ThrowAsync<NexusApiKeyRejectedException>();
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
    }

    [Fact]
    public async Task APinnedKeyIsReportedWithoutAskingNexus()
    {
        var overlay = new OverlaySecretStore(_store, new FakeEnvironment((NexusSecretKeys.ApiKeyEnvironmentVariable, "pinned")), null,
            new Dictionary<SecretKey, string> { [NexusSecretKeys.ApiKey] = NexusSecretKeys.ApiKeyEnvironmentVariable });

        await LoginVerbs.StoreApiKeyAsync(Factory(overlay), _client, _renderer, new StringReader("typed\n"), CancellationToken.None);

        await _client.DidNotReceiveWithAnyArgs().Validate(default!, default);
    }

    [Fact]
    public async Task WhenNexusCannotBeReachedNothingIsStoredAndTheVerbFails()
    {
        _client.Validate(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<Response<ValidateInfo>>>(_ => throw new HttpRequestException("no route to host"));

        var exit = await LoginVerbs.StoreApiKeyAsync(Factory(_store), _client, _renderer, new StringReader("typed\n"), CancellationToken.None);

        exit.Should().Be(1);
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
        _renderer.Lines.Should().ContainSingle().Which.Should().Contain("no route to host");
    }

    [Fact]
    public void TheVerbHasNoApiKeyOption()
    {
        // The key must never travel on the command line, so there is no option to carry it.
        var verb = typeof(LoginVerbs).GetMethod("SetApiKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        verb.GetParameters().SelectMany(p => p.GetCustomAttributes(false)).OfType<SilverModManager.Sdk.ProxyConsole.OptionAttribute>().Should().BeEmpty();
    }
}
