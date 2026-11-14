using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Library.GitHub;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.GitHub;

public class GitHubShelfEntriesTests(IConnection connection)
{
    private static readonly GitHubShelfEntryDefinition Skse = new("ianpatt", "skse64", ".7z", "skse64_", "Script extender");

    [Theory]
    [InlineData("ianpatt/skse64", "ianpatt", "skse64")]
    [InlineData("https://github.com/ianpatt/skse64", "ianpatt", "skse64")]
    [InlineData("https://github.com/ianpatt/skse64/releases", "ianpatt", "skse64")]
    [InlineData("https://github.com/ianpatt/skse64/releases/tag/v2.2.6", "ianpatt", "skse64")]
    [InlineData("github.com/ianpatt/skse64.git", "ianpatt", "skse64")]
    [InlineData("  ianpatt/skse64/ ", "ianpatt", "skse64")]
    public void AddByUrlParsesOwnerAndRepo(string text, string owner, string repository)
    {
        GitHubShelfEntries.TryParseRepository(text, out var parsedOwner, out var parsedRepository).Should().BeTrue();

        parsedOwner.Should().Be(owner);
        parsedRepository.Should().Be(repository);
    }

    [Theory]
    [InlineData("")]
    [InlineData("skse64")]
    [InlineData("https://example.com/ianpatt/skse64")]
    [InlineData("ian patt/skse64")]
    [InlineData("../skse64")]
    [InlineData("a/b?x=1")]
    public void TextThatNamesNoRepositoryIsRefused(string text)
    {
        GitHubShelfEntries.TryParseRepository(text, out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task AddingRecordsARowThatTheGameThenLists()
    {
        var rig = new ShelfRig(connection);

        await rig.Entries.Add(rig.GameId, "https://github.com/ianpatt/skse64/releases", ".7z", "skse64_", includePrereleases: false);

        var listed = rig.Entries.For(rig.Game).Should().ContainSingle().Subject;
        listed.Owner.Should().Be("ianpatt");
        listed.Repository.Should().Be("skse64");
        listed.AssetSuffix.Should().Be(".7z");
        listed.NameHint.Should().Be("skse64_");
        listed.Declared.Should().BeFalse();
    }

    [Fact]
    public async Task AddingARepositoryThatIsAlreadyOnTheShelfIsRefused()
    {
        var rig = new ShelfRig(connection, Skse);

        var act = async () => await rig.Entries.Add(rig.GameId, "IanPatt/SKSE64", ".7z", null, false);

        (await act.Should().ThrowAsync<UserFacingException>()).Which.Message.Should().Contain("ianpatt/skse64");
    }

    [Fact]
    public async Task AddingWithoutAnAssetSuffixIsRefused()
    {
        var rig = new ShelfRig(connection);

        var act = async () => await rig.Entries.Add(rig.GameId, "o/r", "  ", null, false);

        await act.Should().ThrowAsync<UserFacingException>();
        rig.Entries.For(rig.Game).Should().BeEmpty();
    }

    [Fact]
    public async Task ADeclaredEntryAndARowForTheSameRepositoryAreOneEntryWithTheDeclaredAsset()
    {
        var rig = new ShelfRig(connection, Skse);
        await rig.Entries.Remember(rig.Entries.For(rig.Game).Single(), 77UL);

        var listed = rig.Entries.For(rig.Game).Should().ContainSingle().Subject;

        listed.RepositoryId.Should().Be(77UL);
        listed.AssetSuffix.Should().Be(".7z");
        listed.Role.Should().Be("Script extender");
        listed.Declared.Should().BeTrue();
    }

    [Fact]
    public async Task ARowOnlyListsForItsOwnGame()
    {
        var rig = new ShelfRig(connection);
        await rig.Entries.Add(GameId.From("Tests.Shelf.Other." + Guid.NewGuid().ToString("N")), "o/r", ".zip", null, false);

        rig.Entries.For(rig.Game).Should().BeEmpty();
    }

    [Fact]
    public async Task AnItemIsFoundByItsRepositoryIdWhenItWasAddedWithoutAName()
    {
        var rig = new ShelfRig(connection, Skse with { RepositoryId = 9001UL });
        var fixtures = new Lock.LockFixtures(connection);
        var digest = Lock.LockFixtures.DigestOf("x" + Guid.NewGuid());
        var file = await fixtures.AddGitHubFile("skse64_2_02_05.7z", "x" + Guid.NewGuid(), digest, "v2.2.5", repositoryId: 9001UL);

        var found = rig.Entries.Find(GitHubReleaseLibraryFile.Load(connection.Db, file.Id));

        found.Should().NotBeNull();
        found!.Name.Should().Be("ianpatt/skse64");
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels();
        }
    }
}
