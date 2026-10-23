using FluentAssertions;
using NexusMods.Abstractions.Secrets;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Games.FromSoft.Tests.Secrets;

public class CoopPasswordVerbTests
{
    private readonly InMemorySecretStore _store = new();
    private readonly RecordingRenderer _renderer = new();

    private sealed class RecordingRenderer : IRenderer
    {
        public List<string> Lines { get; } = [];

        public ValueTask RenderAsync(IRenderable renderable)
        {
            if (renderable is Text text) Lines.Add(text.Template);
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync() => ValueTask.CompletedTask;
    }

    private Task<int> Run(string stdin, bool clear = false)
        => CoopPasswordVerb.Run(_renderer, _store, new StringReader(stdin), clear, CancellationToken.None);

    private async Task<SecretResult> Stored() => await _store.GetAsync(FromSoftSecretKeys.CoopPassword, CancellationToken.None);

    [Fact]
    public async Task StoresTheTrimmedLineFromStdin()
    {
        var exit = await Run("  hunter2 \nsecond line\n");

        exit.Should().Be(0);
        (await Stored()).Should().Be(new SecretResult.Found("hunter2"));
    }

    [Fact]
    public async Task AnEmptyStdinStoresNothing()
    {
        var exit = await Run("\n");

        exit.Should().Be(1);
        (await Stored()).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task ClearRemovesTheStoredPassword()
    {
        await Run("hunter2");

        var exit = await Run("", clear: true);

        exit.Should().Be(0);
        (await Stored()).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task AnUnavailableStoreExitsOneAndSaysWhy()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "the wallet is locked");

        var exit = await Run("hunter2");

        exit.Should().Be(1);
        _renderer.Lines.Should().Contain(l => l.Contains("the wallet is locked"));
    }

    [Fact]
    public async Task APinnedPasswordExitsOneAndNamesTheSource()
    {
        var store = new ReadOnlyStore("SILVERMM_SECRET_FROMSOFT_ERSC_PASSWORD");

        var exit = await CoopPasswordVerb.Run(_renderer, store, new StringReader("hunter2"), false, CancellationToken.None);

        exit.Should().Be(1);
        _renderer.Lines.Should().Contain(l => l.Contains("SILVERMM_SECRET_FROMSOFT_ERSC_PASSWORD"));
    }

    private sealed class ReadOnlyStore(string source) : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        public ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken ct) => _inner.GetAsync(key, ct);
        public ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken ct) => new(new SecretWriteResult.ReadOnly(source));
        public ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken ct) => new(new SecretWriteResult.ReadOnly(source));
        public ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken ct) => _inner.ListAsync(ct);
        public ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken ct) => _inner.DescribeAsync(ct);
        public ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken ct) => _inner.UnlockAsync(ct);
        public R3.Observable<SecretKey> Changed => _inner.Changed;
        public R3.Observable<SecretStoreStatus> StatusChanged => _inner.StatusChanged;
    }
}
