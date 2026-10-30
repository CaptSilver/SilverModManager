using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// The Apply button reads Current without building a sync tree only while nothing the apply used has changed since it
/// ran. What counts is the loadout's own inputs, not how far the database head has moved.
/// </summary>
public class StatusDecisionTests
{
    private static readonly LoadoutId Loadout = LoadoutId.From(EntityId.From(0x200000000000001));
    private static readonly LoadoutId Other = LoadoutId.From(EntityId.From(0x200000000000002));
    private static readonly TxId Applied = TxId.From(0x100000000000010);
    private static readonly TxId Earlier = TxId.From(0x10000000000000C);
    private static readonly TxId Later = TxId.From(0x100000000000030);

    private static StatusVerdict Decide(
        SyncInputs now,
        SyncInputs atApply,
        bool busy = false,
        bool exists = true,
        LoadoutId? appliedLoadout = null,
        TxId? scanTx = null,
        bool leftUnextracted = false)
        => StatusDecision.Decide(
            busy, exists, Loadout,
            new LoadoutWithTxId(appliedLoadout ?? Loadout, Applied),
            scanTx ?? Applied,
            () => now, () => atApply,
            leftUnextracted);

    [Fact]
    public void InputsUnchangedSinceTheApplyAreCurrentWhateverTheDatabaseHead()
    {
        // Other loadouts, diagnostics and the library have committed since: the head is far ahead of the apply,
        // the loadout's newest datom and datom count are what they were when it ran.
        Decide(new SyncInputs(Earlier, 40), new SyncInputs(Earlier, 40)).Should().Be(StatusVerdict.Current);
    }

    [Fact]
    public void TheApplysOwnLastTransactionMayBeNewerThanTheNewestInput()
    {
        Decide(new SyncInputs(Applied, 40), new SyncInputs(Applied, 40)).Should().Be(StatusVerdict.Current);
    }

    [Fact]
    public void ANewerInputDatomDeclinesTheShortcut()
    {
        Decide(new SyncInputs(Later, 41), new SyncInputs(Applied, 40)).Should().Be(StatusVerdict.Compare);
    }

    [Fact]
    public void ASwapOfOneDatomForAnotherDeclinesTheShortcutThroughTheNewerTransaction()
    {
        Decide(new SyncInputs(Later, 40), new SyncInputs(Applied, 40)).Should().Be(StatusVerdict.Compare);
    }

    [Fact]
    public void FewerInputDatomsDeclineTheShortcutEvenWhenNothingIsNewer()
    {
        // Removing the only mod that provides some files retracts datoms and asserts none.
        Decide(new SyncInputs(Earlier, 30), new SyncInputs(Earlier, 40)).Should().Be(StatusVerdict.Compare);
    }

    [Fact]
    public void ARescanSinceTheApplyDeclinesTheShortcut()
    {
        Decide(new SyncInputs(Earlier, 40), new SyncInputs(Earlier, 40), scanTx: Later).Should().Be(StatusVerdict.Compare);
    }

    [Fact]
    public void AnApplyThatLeftFilesUnextractedDeclinesTheShortcutWhateverTheInputs()
    {
        // The archive that was missing is not a datom of the loadout, so the inputs look untouched.
        Decide(new SyncInputs(Earlier, 40), new SyncInputs(Earlier, 40), leftUnextracted: true).Should().Be(StatusVerdict.Compare);
    }

    [Fact]
    public void AnotherLoadoutAppliedIsOtherLoadoutSynced()
    {
        Decide(new SyncInputs(Earlier, 40), new SyncInputs(Earlier, 40), appliedLoadout: Other).Should().Be(StatusVerdict.OtherLoadoutSynced);
    }

    [Fact]
    public void ADeletedLoadoutIsOtherLoadoutSynced()
    {
        Decide(new SyncInputs(Earlier, 40), new SyncInputs(Earlier, 40), exists: false).Should().Be(StatusVerdict.OtherLoadoutSynced);
    }

    [Fact]
    public void AnApplyInProgressIsPending()
    {
        Decide(new SyncInputs(Later, 99), new SyncInputs(Applied, 40), busy: true).Should().Be(StatusVerdict.Pending);
    }

    [Fact]
    public void ALoadoutNeverAppliedHasToBeCompared()
    {
        var verdict = StatusDecision.Decide(false, true, Loadout, default, default, () => new SyncInputs(Earlier, 40), () => new SyncInputs(Earlier, 40));

        verdict.Should().Be(StatusVerdict.Compare);
    }

    [Fact]
    public void TheInputsAreNotReadWhenTheyCannotChangeTheAnswer()
    {
        var read = false;
        SyncInputs Read() { read = true; return default; }

        StatusDecision.Decide(true, true, Loadout, new LoadoutWithTxId(Loadout, Applied), Applied, Read, Read);
        StatusDecision.Decide(false, true, Loadout, new LoadoutWithTxId(Other, Applied), Applied, Read, Read);
        StatusDecision.Decide(false, true, Loadout, default, default, Read, Read);

        read.Should().BeFalse();
    }
}
