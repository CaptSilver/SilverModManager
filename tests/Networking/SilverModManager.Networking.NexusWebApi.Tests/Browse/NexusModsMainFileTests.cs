using FluentAssertions;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi.Tests.Browse;

public class NexusModsMainFileTests
{
    private static NexusModsFileRow File(uint id, NexusModsFileCategory category) => new(FileId.From(id), $"file {id}", "1.0", category, 1024, DateTimeOffset.UnixEpoch);

    [Fact]
    public void ASingleMainFile_IsChosen()
    {
        var chosen = NexusModsMainFile.Pick([File(1, NexusModsFileCategory.Optional), File(2, NexusModsFileCategory.Main), File(3, NexusModsFileCategory.OldVersion)]);

        chosen.Should().Be(FileId.From(2));
    }

    [Fact]
    public void NoMainFile_ChoosesNothing()
    {
        NexusModsMainFile.Pick([File(1, NexusModsFileCategory.Optional), File(2, NexusModsFileCategory.Miscellaneous)]).Should().BeNull();
    }

    [Fact]
    public void NoFiles_ChoosesNothing()
    {
        NexusModsMainFile.Pick([]).Should().BeNull();
    }

    [Fact]
    public void SeveralMainFiles_ChooseNothingRatherThanGuess()
    {
        NexusModsMainFile.Pick([File(1, NexusModsFileCategory.Main), File(2, NexusModsFileCategory.Main)]).Should().BeNull();
    }

    [Fact]
    public void FilesThatAreNoLongerCurrent_AreNotCandidates()
    {
        var chosen = NexusModsMainFile.Pick([File(1, NexusModsFileCategory.Archived), File(2, NexusModsFileCategory.Removed), File(3, NexusModsFileCategory.Main)]);

        chosen.Should().Be(FileId.From(3));
    }

    [Fact]
    public void MainCount_CountsOnlyTheMainFiles()
    {
        NexusModsMainFile.MainCount([File(1, NexusModsFileCategory.Main), File(2, NexusModsFileCategory.Optional), File(3, NexusModsFileCategory.Main)]).Should().Be(2);
        NexusModsMainFile.MainCount([]).Should().Be(0);
    }
}
