using Xunit;
using FluentAssertions;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Secrets.Overlay;
using SilverModManager.Secrets.Verbs;

namespace SilverModManager.Secrets.Tests.Verbs;

public class SecretVerbsTests
{
    private static readonly SecretKey ApiKey = new("nexus.api-key");
    private static readonly SecretKey OAuth = new("nexus.oauth");
    private const string StoredValue = "stored-value-that-must-not-print";

    private readonly InMemorySecretStore _inner = new();
    private readonly FakeEnvironment _env = new();
    private readonly RecordingRenderer _renderer = new();

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

    private OverlaySecretStore Overlay() => new(_inner, _env, file: null, new Dictionary<SecretKey, string> { [ApiKey] = "NEXUS_API_KEY" }, [ApiKey, OAuth]);

    [Fact]
    public async Task ReportsTheBackendItsStateAndTheKeyNamesButNeverAValue()
    {
        await _inner.SetAsync(OAuth, StoredValue, CancellationToken.None);
        _inner.Status = new SecretStoreStatus(SecretBackendKind.File, "private file /x/secrets", Locked: false, Unencrypted: true, Writable: true, []);

        var exit = await SecretVerbs.ReportAsync(Overlay(), _renderer, CancellationToken.None);

        exit.Should().Be(0);
        _renderer.Lines.Should().Contain("Backend: File");
        _renderer.Lines.Should().Contain("Detail: private file /x/secrets");
        _renderer.Lines.Should().Contain("Locked: no");
        _renderer.Lines.Should().Contain("Writable: yes");
        _renderer.Lines.Should().Contain("Unencrypted: yes");
        _renderer.Lines.Should().Contain("Keys: nexus.oauth");
        string.Join('\n', _renderer.Lines).Should().NotContain(StoredValue);
    }

    [Fact]
    public async Task SaysSoWhenTheStoreHoldsNoKeys()
    {
        await SecretVerbs.ReportAsync(Overlay(), _renderer, CancellationToken.None);

        _renderer.Lines.Should().Contain("Keys: none");
        _renderer.Lines.Should().Contain("Overridden: none");
    }

    [Fact]
    public async Task NamesTheKeysAnOverridePinsWithTheirSourceButNotTheirValue()
    {
        _env.Values["NEXUS_API_KEY"] = "env-value-that-must-not-print";

        await SecretVerbs.ReportAsync(Overlay(), _renderer, CancellationToken.None);

        _renderer.Lines.Should().Contain("Overridden: nexus.api-key (environment variable NEXUS_API_KEY)");
        string.Join('\n', _renderer.Lines).Should().NotContain("env-value-that-must-not-print");
    }

    [Fact]
    public async Task ReportsALockedStoreWithoutPromptingForIt()
    {
        _inner.Status = new SecretStoreStatus(SecretBackendKind.SecretService, "KWallet", Locked: true, Unencrypted: false, Writable: false, []);

        await SecretVerbs.ReportAsync(Overlay(), _renderer, CancellationToken.None);

        _renderer.Lines.Should().Contain("Locked: yes");
        _renderer.Lines.Should().Contain("Writable: no");
        _inner.Calls.Should().NotContain("Unlock");
    }
}
