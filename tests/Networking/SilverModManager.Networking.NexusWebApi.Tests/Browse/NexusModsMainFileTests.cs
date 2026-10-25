using FluentAssertions;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi.Tests.Browse;

public class NexusModsMainFileTests
{
    private static NexusModsFileSummary File(uint id, ModFileCategory category) => new(FileId.From(id), category);

    [Fact]
    public void ASingleMainFile_IsChosen()
    {
        var chosen = NexusModsMainFile.Pick([File(1, ModFileCategory.Optional), File(2, ModFileCategory.Main), File(3, ModFileCategory.OldVersion)]);

        chosen.Should().Be(FileId.From(2));
    }

    [Fact]
    public void NoMainFile_ChoosesNothing()
    {
        NexusModsMainFile.Pick([File(1, ModFileCategory.Optional), File(2, ModFileCategory.Miscellaneous)]).Should().BeNull();
    }

    [Fact]
    public void NoFiles_ChoosesNothing()
    {
        NexusModsMainFile.Pick([]).Should().BeNull();
    }

    [Fact]
    public void SeveralMainFiles_ChooseNothingRatherThanGuess()
    {
        NexusModsMainFile.Pick([File(1, ModFileCategory.Main), File(2, ModFileCategory.Main)]).Should().BeNull();
    }

    [Fact]
    public void FilesThatAreNoLongerCurrent_AreNotCandidates()
    {
        var chosen = NexusModsMainFile.Pick([File(1, ModFileCategory.Archived), File(2, ModFileCategory.Removed), File(3, ModFileCategory.Main)]);

        chosen.Should().Be(FileId.From(3));
    }
}
