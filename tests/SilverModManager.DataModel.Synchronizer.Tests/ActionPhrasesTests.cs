using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// The summary line of a pass names what is pending in words, not as the enum's flag list.
/// </summary>
public class ActionPhrasesTests
{
    [Theory]
    [InlineData(Actions.BackupFile, "back up")]
    [InlineData(Actions.IngestFromDisk, "ingest")]
    [InlineData(Actions.DeleteFromDisk, "delete")]
    [InlineData(Actions.ExtractToDisk, "extract")]
    [InlineData(Actions.WriteIntrinsic, "write")]
    [InlineData(Actions.AdaptLoadout, "adapt the loadout")]
    [InlineData(Actions.AddReifiedDelete, "record a delete")]
    [InlineData(Actions.WarnOfUnableToExtract, "warn of a missing archive")]
    [InlineData(Actions.WarnOfConflict, "warn of a conflict")]
    [InlineData(Actions.ForgetDiskState, "stop tracking")]
    public void ASingleFlagIsOneWord(Actions actions, string expected) => ActionPhrases.Describe(actions).Should().Be(expected);

    [Fact]
    public void TwoFlagsAreJoinedWithAnd() => ActionPhrases.Describe(Actions.BackupFile | Actions.DeleteFromDisk).Should().Be("back up and delete");

    [Fact]
    public void ThreeFlagsAreListedWithCommas() =>
        ActionPhrases.Describe(Actions.BackupFile | Actions.IngestFromDisk | Actions.DeleteFromDisk).Should().Be("back up, ingest and delete");

    [Fact]
    public void TheOrderFollowsTheFlagsNotTheAlphabet() =>
        ActionPhrases.Describe(Actions.WriteIntrinsic | Actions.BackupFile).Should().Be("back up and write");

    [Fact]
    public void SummarizeCountsEachPhraseAndListsTheBiggestGroupFirst() =>
        ActionPhrases.Summarize([Actions.WriteIntrinsic, Actions.ExtractToDisk, Actions.ExtractToDisk]).Should().Equal("extract: 2", "write: 1");

    [Fact]
    public void SummarizeOrdersGroupsOfEqualSizeByPhraseWhateverTheInputOrder()
    {
        ActionPhrases.Summarize([Actions.WriteIntrinsic, Actions.ExtractToDisk]).Should().Equal("extract: 1", "write: 1");
        ActionPhrases.Summarize([Actions.ExtractToDisk, Actions.WriteIntrinsic]).Should().Equal("extract: 1", "write: 1");
    }

    [Fact]
    public void SummarizeGroupsDifferentFlagValuesThatReadTheSame() =>
        ActionPhrases.Summarize([Actions.DoNothing, (Actions)0]).Should().Equal("no action: 2");

    [Fact]
    public void NoFlagsIsNoAction() => ActionPhrases.Describe((Actions)0).Should().Be("no action");

    [Fact]
    public void DoNothingIsNoAction() => ActionPhrases.Describe(Actions.DoNothing).Should().Be("no action");

    [Fact]
    public void EveryFlagHasItsOwnPhrase()
    {
        var phrases = Enum.GetValues<Actions>().Select(flag => ActionPhrases.Describe(flag)).ToArray();
        phrases.Should().OnlyHaveUniqueItems();
        phrases.Should().NotContain(p => p.Contains("unknown action"));
    }

    [Fact]
    public void ABitWithNoNameIsStillShown() => ActionPhrases.Describe(Actions.WriteIntrinsic | (Actions)(1 << 15)).Should().Be("write and unknown action");
}
