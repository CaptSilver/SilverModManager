using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

public class TransactionPinsTests
{
    private static FileIdentity Identity(ulong value) => FileIdentity.XxHash3((Hash)value);

    [Fact]
    public void Keep_HoldsThePinUntilTheHoldIsDisposed()
    {
        var tx = Substitute.For<ITransaction>();
        var identity = Identity(0x7A5E_0001UL);
        var hold = TransactionPins.Hold(tx);

        TransactionPins.Keep(tx, FileStorePins.Pin(identity));
        FileStorePins.Pinned().Should().Contain(identity);

        hold.Dispose();
        FileStorePins.Pinned().Should().NotContain(identity);
    }

    [Fact]
    public void Keep_WithoutAHoldDisposesThePinAndThrows()
    {
        var tx = Substitute.For<ITransaction>();
        var identity = Identity(0x7A5E_0002UL);

        var act = () => TransactionPins.Keep(tx, FileStorePins.Pin(identity));

        act.Should().Throw<InvalidOperationException>();
        FileStorePins.Pinned().Should().NotContain(identity, "a pin nobody would release is dropped, not leaked");
    }

    [Fact]
    public void Hold_ReleasesOnlyItsOwnTransactionsPins()
    {
        var a = Substitute.For<ITransaction>();
        var b = Substitute.For<ITransaction>();
        var ia = Identity(0x7A5E_0003UL);
        var ib = Identity(0x7A5E_0004UL);
        using var holdB = TransactionPins.Hold(b);
        var holdA = TransactionPins.Hold(a);
        TransactionPins.Keep(a, FileStorePins.Pin(ia));
        TransactionPins.Keep(b, FileStorePins.Pin(ib));

        holdA.Dispose();

        FileStorePins.Pinned().Should().NotContain(ia).And.Contain(ib);
    }

    [Fact]
    public void Hold_OpenedTwiceForOneTransactionThrows()
    {
        var tx = Substitute.For<ITransaction>();
        using var first = TransactionPins.Hold(tx);

        var act = () => TransactionPins.Hold(tx);

        act.Should().Throw<InvalidOperationException>();
    }
}
