using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

public class ArchiveVersionsAtApplyTests
{
    private static readonly GameInstallMetadataId Installation = GameInstallMetadataId.From(EntityId.From(0x200000000000001));
    private static readonly TxId FirstApply = TxId.From(0x100000000000010);
    private static readonly TxId SecondApply = TxId.From(0x100000000000020);

    [Fact]
    public void TheVersionRecordedForAnApplyIsReturnedForThatApply()
    {
        var versions = new ArchiveVersionsAtApply();

        versions.Record(Installation, FirstApply, 7);

        versions.For(Installation, FirstApply).Should().Be(7);
    }

    // The status reads the applied transaction from the database; a record from another Apply says nothing about it.
    [Fact]
    public void ARecordForAnotherApplyIsNoAnswer()
    {
        var versions = new ArchiveVersionsAtApply();

        versions.Record(Installation, FirstApply, 7);

        versions.For(Installation, SecondApply).Should().BeNull();
    }

    [Fact]
    public void TheLatestApplyReplacesTheEarlierRecord()
    {
        var versions = new ArchiveVersionsAtApply();

        versions.Record(Installation, FirstApply, 7);
        versions.Record(Installation, SecondApply, 9);

        versions.For(Installation, SecondApply).Should().Be(9);
        versions.For(Installation, FirstApply).Should().BeNull();
    }

    [Fact]
    public void AnInstallationThisProcessNeverAppliedHasNoRecord()
        => new ArchiveVersionsAtApply().For(Installation, FirstApply).Should().BeNull();
}
