using FluentAssertions;
using NexusMods.Backend.Games.UserGames;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Tests.Games;

/// <summary>
/// The add-game lines no shipped pair of games reaches through the CLI: no two of them launch one executable with
/// nothing to tell them apart, and the CLI host detects no installs.
/// </summary>
public class AddGameVerbsDescribeTests
{
    private readonly AbsolutePath _folder = new InMemoryFileSystem(OSInformation.FakeUnix).FromUnsanitizedFullPath("/games/shared");

    [Fact]
    public void AmbiguityNamesEveryCandidateAndTheGameOption()
    {
        var result = new AddGameResult.Ambiguous([
            new AddGameResult.AmbiguousCandidate(new FakeGameData("first"), RelativePath.FromUnsanitizedInput("First.exe")),
            new AddGameResult.AmbiguousCandidate(new FakeGameData("second"), RelativePath.FromUnsanitizedInput("Second.exe")),
        ]);

        AddGameVerbs.Describe(result, _folder).Should().Be(
            (1, "Several games fit /games/shared: first (first, launches First.exe), second (second, launches Second.exe); pass --game <id>"));
    }

    [Fact]
    public void AnUnreadableFolderNamesTheFolderAndTheError()
    {
        var result = new AddGameResult.FolderUnreadable(_folder, "Access to the path is denied.");

        AddGameVerbs.Describe(result, _folder).Should().Be((1, "Could not list /games/shared: Access to the path is denied."));
    }

    [Fact]
    public void AnAlreadyDetectedFolderNamesTheStoreAndGame()
    {
        var result = new AddGameResult.AlreadyDetected(new FakeGameData("alpha"), GameStore.ManuallyAdded);

        AddGameVerbs.Describe(result, _folder).Should().Be((1, "/games/shared is already detected as a Manually Added install of alpha"));
    }
}
