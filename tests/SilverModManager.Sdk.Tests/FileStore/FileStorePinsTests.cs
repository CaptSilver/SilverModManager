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
}
