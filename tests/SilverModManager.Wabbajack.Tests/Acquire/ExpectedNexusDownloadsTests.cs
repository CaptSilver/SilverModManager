using FluentAssertions;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class ExpectedNexusDownloadsTests
{
    private static readonly GameDomain Domain = GameDomain.From("skyrimspecialedition");
    private static readonly GameId SkyrimVr = GameId.From("CreationEngine.SkyrimVR");
    private static readonly (GameDomain, ModId, FileId) File = (Domain, ModId.From(5), FileId.From(6));

    [Fact]
    public void ExpectedNexusDownloads_TryGetExpectedGame_ReturnsTheOwnersGame()
    {
        var expected = new ExpectedNexusDownloads();
        expected.Set(new object(), [File], SkyrimVr);

        expected.TryGetExpectedGame(Domain, ModId.From(5), FileId.From(6), out var game).Should().BeTrue();
        game.Should().Be(SkyrimVr);
    }

    [Fact]
    public void ExpectedNexusDownloads_TryGetExpectedGame_NobodyExpectsTheFile_IsFalse()
    {
        var expected = new ExpectedNexusDownloads();
        expected.Set(new object(), [File], SkyrimVr);

        expected.TryGetExpectedGame(Domain, ModId.From(5), FileId.From(7), out _).Should().BeFalse();
    }

    [Fact]
    public void ExpectedNexusDownloads_TryGetExpectedGame_OwnerWithoutAGame_IsFalseButStillExpected()
    {
        var expected = new ExpectedNexusDownloads();
        expected.Set(new object(), [File], game: null);

        expected.TryGetExpectedGame(Domain, ModId.From(5), FileId.From(6), out _).Should().BeFalse();
        expected.IsExpected(Domain, ModId.From(5), FileId.From(6)).Should().BeTrue();
    }

    [Fact]
    public void ExpectedNexusDownloads_TryGetExpectedGame_SkipsAnOwnerWithoutAGameForOneThatHasIt()
    {
        var expected = new ExpectedNexusDownloads();
        expected.Set(new object(), [File], game: null);
        expected.Set(new object(), [File], SkyrimVr);

        expected.TryGetExpectedGame(Domain, ModId.From(5), FileId.From(6), out var game).Should().BeTrue();
        game.Should().Be(SkyrimVr);
    }

    [Fact]
    public void ExpectedNexusDownloads_TryGetExpectedGame_AfterTheOwnerIsCleared_IsFalse()
    {
        var expected = new ExpectedNexusDownloads();
        var owner = new object();
        expected.Set(owner, [File], SkyrimVr);

        expected.Clear(owner);

        expected.TryGetExpectedGame(Domain, ModId.From(5), FileId.From(6), out _).Should().BeFalse();
    }
}
