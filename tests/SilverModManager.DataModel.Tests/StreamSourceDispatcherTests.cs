using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using Xunit;

namespace SilverModManager.DataModel.Tests;

public class StreamSourceDispatcherTests
{
    private static readonly FileIdentity Sha1 = FileIdentity.Sha1(Sha1Value.FromHex(new string('B', 40)));
    private static readonly FileIdentity XxHash3 = FileIdentity.XxHash3(new byte[] { 1, 2, 3 }.xxHash3());

    private class FakeSource(SourcePriority priority, bool has) : IReadOnlyStreamSource
    {
        public List<FileIdentity> Asked { get; } = [];

        public ValueTask<Stream?> OpenAsync(FileIdentity identity, CancellationToken cancellationToken = default)
        {
            Asked.Add(identity);
            return ValueTask.FromResult<Stream?>(has ? new MemoryStream([1]) : null);
        }

        public bool Exists(FileIdentity identity)
        {
            Asked.Add(identity);
            return has;
        }

        public SourcePriority Priority => priority;
    }

    private sealed class FakeIdentitySource(SourcePriority priority, bool has) : FakeSource(priority, has), IIdentityStreamSource;

    [Fact]
    public async Task OpenAsync_Sha1Identity_NeverAsksAPlainXxHash3Source()
    {
        var plain = new FakeSource(SourcePriority.Backup, has: true);
        var identity = new FakeIdentitySource(SourcePriority.Local, has: true);
        var dispatcher = new StreamSourceDispatcher([plain, identity]);

        await using var stream = await dispatcher.OpenAsync(Sha1, TestContext.Current.CancellationToken);

        stream.Should().NotBeNull();
        plain.Asked.Should().BeEmpty("a source keyed only by xxHash3 cannot answer a SHA-1");
        identity.Asked.Should().ContainSingle().Which.Should().Be(Sha1);
    }

    [Fact]
    public void Exists_Sha1Identity_NeverAsksAPlainXxHash3Source()
    {
        var plain = new FakeSource(SourcePriority.Backup, has: true);
        var identity = new FakeIdentitySource(SourcePriority.Local, has: false);
        var dispatcher = new StreamSourceDispatcher([plain, identity]);

        dispatcher.Exists(Sha1).Should().BeFalse("only the identity source was asked, and it does not have the file");
        plain.Asked.Should().BeEmpty();
        identity.Asked.Should().ContainSingle();
    }

    [Fact]
    public void Exists_Sha1Identity_WithOnlyPlainSources_IsFalse()
    {
        var plain = new FakeSource(SourcePriority.Backup, has: true);
        var dispatcher = new StreamSourceDispatcher([plain]);

        dispatcher.Exists(Sha1).Should().BeFalse();
        plain.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenAsync_XxHash3Identity_AsksEverySourceInPriorityOrder()
    {
        var plain = new FakeSource(SourcePriority.Backup, has: false);
        var identity = new FakeIdentitySource(SourcePriority.Local, has: true);
        var dispatcher = new StreamSourceDispatcher([identity, plain]);

        await using var stream = await dispatcher.OpenAsync(XxHash3, TestContext.Current.CancellationToken);

        stream.Should().NotBeNull();
        plain.Asked.Should().ContainSingle("the lower-priority-number source goes first");
        identity.Asked.Should().ContainSingle();
    }
}
