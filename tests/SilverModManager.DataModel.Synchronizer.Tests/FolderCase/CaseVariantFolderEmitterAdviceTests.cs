using FluentAssertions;
using SilverModManager.DataModel.Diagnostics;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests.FolderCase;

public class CaseVariantFolderEmitterAdviceTests
{
    [Fact]
    public void AdviceFor_MergesOnApply_SaysWhatTheApplyDoesAndWhatStaysForTheUser()
    {
        var advice = CaseVariantFolderEmitter.AdviceFor(mergedOnApply: true);

        advice.Should().Contain("The next Apply merges each pair into one folder")
            .And.Contain("different contents").And.Contain("saves").And.Contain("move or delete those by hand");
    }

    [Fact]
    public void AdviceFor_ByHand_TellsTheUserToMoveTheFiles()
    {
        CaseVariantFolderEmitter.AdviceFor(mergedOnApply: false).Should().Be("Move the files of one folder of the pair into the other, or delete them.");
    }
}
