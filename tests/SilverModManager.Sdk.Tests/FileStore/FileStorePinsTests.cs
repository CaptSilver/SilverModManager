using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Sdk.Tests.FileStore;

public class FileStorePinsTests
{
    [Fact]
    public void Pin_HoldsAnXxHash3IdentityUntilDisposed()
    {
        var identity = FileIdentity.XxHash3((Hash)0x5150_0001UL);
        var pin = FileStorePins.Pin(identity);
        FileStorePins.Pinned().Should().Contain(identity);

        pin.Dispose();
        FileStorePins.Pinned().Should().NotContain(identity);
    }

    [Fact]
    public void Pin_RejectsAnIdentityThatIsNotXxHash3()
    {
        var sha1 = FileIdentity.Sha1(Sha1Value.From(new byte[20]));

        var act = () => FileStorePins.Pin(sha1);

        act.Should().Throw<InvalidOperationException>();
        FileStorePins.Pinned().Should().NotContain(sha1);
    }

    [Fact]
    public void Collection_ReportsAPinThatWasTakenAndReleasedAfterItBegan()
    {
        var before = FileIdentity.XxHash3((Hash)0x5150_0010UL);
        var during = FileIdentity.XxHash3((Hash)0x5150_0011UL);
        var heldAtStart = FileStorePins.Pin(before);
        using var collection = FileStorePins.BeginCollection();
        heldAtStart.Dispose();

        FileStorePins.Pin(during).Dispose();

        collection.Seal().Should().BeEquivalentTo([before, during]);
    }

    [Fact]
    public void Collection_IsNotAffectedByAPinReleasedBeforeItBegan()
    {
        var identity = FileIdentity.XxHash3((Hash)0x5150_0020UL);
        FileStorePins.Pin(identity).Dispose();

        using var collection = FileStorePins.BeginCollection();

        collection.Seal().Should().NotContain(identity);
    }

    [Fact]
    public void Pin_WaitsWhileASealedCollectionIsRunningAndSucceedsOnceItIsDisposed()
    {
        var identity = FileIdentity.XxHash3((Hash)0x5150_0030UL);
        var collection = FileStorePins.BeginCollection();
        IDisposable? pin = null;
        Thread? pinner = null;
        try
        {
            collection.Seal();
            pinner = new Thread(() => pin = FileStorePins.Pin(identity));
            pinner.Start();

            // Waiting on the gate is a thread state, not a timing guess. A pin that returns at once ends the thread
            // instead, and the bound turns that into a failure rather than a hung test host.
            SpinWait.SpinUntil(() => pinner.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(30))
                .Should().BeTrue("the pin waits while the collection is sealed");
            FileStorePins.Pinned().Should().NotContain(identity);
        }
        finally
        {
            // A static collection left open would make every later BeginCollection throw.
            collection.Dispose();
        }
        pinner?.Join();

        FileStorePins.Pinned().Should().Contain(identity);
        pin!.Dispose();
    }

    [Fact]
    public void BeginCollection_ThrowsWhileAnotherCollectionIsWatching()
    {
        using var first = FileStorePins.BeginCollection();

        var act = () => FileStorePins.BeginCollection();

        act.Should().Throw<InvalidOperationException>();
    }
}
