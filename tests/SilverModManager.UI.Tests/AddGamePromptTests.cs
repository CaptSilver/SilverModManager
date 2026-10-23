using System.Collections.Immutable;
using DynamicData.Kernel;
using SilverModManager.Sdk;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.NexusModsApi;
using FluentAssertions;
using SilverModManager.App.UI.Pages.MyGames;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests;

public class AddGamePromptTests
{
    private static IGameData Game(string id, string name) => new FakeGame(GameId.From(id), name);

    private sealed class FakeGame(GameId gameId, string displayName) : IGameData
    {
        public GameId GameId { get; } = gameId;
        public StoreIdentifiers StoreIdentifiers => new(GameId);
        public string DisplayName { get; } = displayName;
        public Optional<NexusModsGameId> NexusModsGameId => default;
        public IStreamFactory IconImage => throw new NotSupportedException();
        public IStreamFactory TileImage => throw new NotSupportedException();
        public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
        public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();
    }

    private static readonly AbsolutePath Folder = FileSystem.Shared.FromUnsanitizedFullPath("/games/Fallout 76");

    [Fact]
    public void AddedNamesTheGameAndSaysItIsListed()
    {
        var prompt = AddGamePrompt.For(new AddGameResult.Added(Game("fo76", "Fallout 76")), Folder);

        prompt.Title.Should().Contain("Fallout 76");
        prompt.Markdown.Should().Contain("Fallout 76").And.Contain("listed");
        prompt.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void NeedsPrefixNamesTheGameAndTheCommandLineForm()
    {
        var prompt = AddGamePrompt.For(new AddGameResult.NeedsPrefix(Game("fo76", "Fallout 76")), Folder);

        prompt.Title.Should().Contain("Fallout 76").And.Contain("Wine prefix");
        prompt.Candidates.Should().BeEmpty();
        prompt.Markdown.Should().Contain("Fallout 76");
        prompt.Markdown.Should().Contain("Wine prefix");
        prompt.Markdown.Should().Contain("silvermod add-game");
        prompt.Markdown.Should().Contain("--prefix");
        prompt.Markdown.Should().Contain(Folder.ToString());
    }

    [Fact]
    public void AlreadyDetectedNamesTheStoreAndTheGame()
    {
        var prompt = AddGamePrompt.For(new AddGameResult.AlreadyDetected(Game("fo76", "Fallout 76"), GameStore.GOG), Folder);

        prompt.Title.Should().Contain("Fallout 76").And.Contain("already listed");
        prompt.Candidates.Should().BeEmpty();
        prompt.Markdown.Should().Contain("Fallout 76");
        prompt.Markdown.Should().Contain(GameStore.GOG.Value);
    }

    [Fact]
    public void UnknownNamesTheFolder()
    {
        var prompt = AddGamePrompt.For(new AddGameResult.Unknown(), Folder);

        prompt.Title.Should().Be("No known game found");
        prompt.Candidates.Should().BeEmpty();
        prompt.Markdown.Should().Contain(Folder.ToString());
    }

    [Fact]
    public void StoreUnavailableRelaysTheMessage()
    {
        var prompt = AddGamePrompt.For(new AddGameResult.StoreUnavailable("installs.yaml is read-only"), Folder);

        prompt.Title.Should().Be("Couldn't record the game");
        prompt.Candidates.Should().BeEmpty();
        prompt.Markdown.Should().Contain("installs.yaml is read-only");
    }

    [Fact]
    public void AmbiguousOffersOneButtonPerCandidateCarryingItsGameId()
    {
        var skyrim = Game("skyrim-se", "Skyrim Special Edition");
        var enderal = Game("enderal-se", "Enderal Special Edition");
        var result = new AddGameResult.Ambiguous([
            new AddGameResult.AmbiguousCandidate(skyrim, RelativePath.FromUnsanitizedInput("SkyrimSE.exe")),
            new AddGameResult.AmbiguousCandidate(enderal, RelativePath.FromUnsanitizedInput("Enderal Launcher.exe")),
        ]);

        var prompt = AddGamePrompt.For(result, Folder);

        prompt.Title.Should().Be("Which game is this?");
        prompt.Candidates.Select(c => c.Label).Should().Equal("Skyrim Special Edition", "Enderal Special Edition");
        prompt.Candidates.Select(c => c.Game).Should().Equal(GameId.From("skyrim-se"), GameId.From("enderal-se"));
        prompt.Markdown.Should().Contain("Skyrim Special Edition launches `SkyrimSE.exe`");
        prompt.Markdown.Should().Contain("Enderal Special Edition launches `Enderal Launcher.exe`");
    }

    [Fact]
    public void AnUnreadableFolderNamesTheFolderAndTheError()
    {
        var prompt = AddGamePrompt.For(new AddGameResult.FolderUnreadable(Folder, "Access to the path is denied."), Folder);

        prompt.Candidates.Should().BeEmpty();
        prompt.Markdown.Should().Contain(Folder.ToString()).And.Contain("Access to the path is denied.");
    }
}
