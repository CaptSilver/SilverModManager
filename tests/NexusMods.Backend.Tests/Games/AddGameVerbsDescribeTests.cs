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
        var result = new AddGameResult.Ambiguous("Shared.exe", [new FakeGameData("first"), new FakeGameData("second")]);

        AddGameVerbs.Describe(result, _folder).Should().Be(
            (1, "Several games launch Shared.exe in /games/shared: first (first), second (second); pass --game <id>"));
    }

    [Fact]
    public void AnAlreadyDetectedFolderNamesTheStoreAndGame()
    {
        var result = new AddGameResult.AlreadyDetected(new FakeGameData("alpha"), GameStore.ManuallyAdded);

        AddGameVerbs.Describe(result, _folder).Should().Be((1, "/games/shared is already detected as a Manually Added install of alpha"));
    }
}
