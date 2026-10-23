using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Sdk.NexusModsApi;
using Xunit;

namespace NexusMods.Networking.NexusWebApi.Tests;

public class NexusModsUrlBuilderTests
{
    private const string StardewValley = "stardewvalley";

    [Theory]
    [InlineData(6672467ul, "https://www.nexusmods.com/users/6672467")]
    public void Test_GetProfileUri(ulong userId, string expected)
    {
        var actual = NexusModsUrlBuilder.GetProfileUri(UserId.From(userId)).ToString();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(StardewValley, "https://www.nexusmods.com/games/stardewvalley")]
    public void Test_GetGameUri(string gameDomain, string expected)
    {
        var actual = NexusModsUrlBuilder.GetGameUri(GameDomain.From(gameDomain)).ToString();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(StardewValley, 2400, "https://www.nexusmods.com/stardewvalley/mods/2400")]
    public void Test_GetModUri(string gameDomain, uint modId, string expected)
    {
        var actual = NexusModsUrlBuilder.GetModUri(GameDomain.From(gameDomain), ModId.From(modId)).ToString();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(StardewValley, 2400, 128328, false, "https://www.nexusmods.com/stardewvalley/mods/2400?tab=files&file_id=128328&nmm=0")]
    [InlineData(StardewValley, 2400, 128328, true, "https://www.nexusmods.com/stardewvalley/mods/2400?tab=files&file_id=128328&nmm=1")]
    public void Test_GetFileDownloadUri(string gameDomain, uint modId, uint fileId, bool useNxmLink, string expected)
    {
        var actual = NexusModsUrlBuilder.GetFileDownloadUri(GameDomain.From(gameDomain), ModId.From(modId), FileId.From(fileId), useNxmLink).ToString();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(StardewValley, "tckf0m", null, "https://www.nexusmods.com/games/stardewvalley/collections/tckf0m")]
    [InlineData(StardewValley, "tckf0m", 80ul, "https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/revisions/80")]
    public void Test_GetCollectionUri(string gameDomain, string collectionSlug, ulong? revisionNumber, string expected)
    {
        var actual = NexusModsUrlBuilder.GetCollectionUri(GameDomain.From(gameDomain), CollectionSlug.From(collectionSlug), revisionNumber.HasValue ? RevisionNumber.From(revisionNumber.Value) : default(Optional<RevisionNumber>)).ToString();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(StardewValley, "tckf0m", null, "https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/bugs")]
    [InlineData(StardewValley, "tckf0m", 80ul, "https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/revisions/80/bugs")]
    public void Test_GetCollectionBugsUri(string gameDomain, string collectionSlug, ulong? revisionNumber, string expected)
    {
        var actual = NexusModsUrlBuilder.GetCollectionBugsUri(GameDomain.From(gameDomain), CollectionSlug.From(collectionSlug), revisionNumber.HasValue ? RevisionNumber.From(revisionNumber.Value) : default(Optional<RevisionNumber>)).ToString();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(StardewValley, "tckf0m", null, "https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/changelog")]
    [InlineData(StardewValley, "tckf0m", 80ul, "https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/revisions/80/changelog")]
    public void Test_GetCollectionChangelogUri(string gameDomain, string collectionSlug, ulong? revisionNumber, string expected)
    {
        var actual = NexusModsUrlBuilder.GetCollectionChangelogUri(GameDomain.From(gameDomain), CollectionSlug.From(collectionSlug), revisionNumber.HasValue ? RevisionNumber.From(revisionNumber.Value) : default(Optional<RevisionNumber>)).ToString();
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(StardewValley, "https://www.nexusmods.com/games/stardewvalley/collections")]
    public void Test_GetBrowseCollectionsUri(string gameDomain, string expected)
    {
        var actual = NexusModsUrlBuilder.GetBrowseCollectionsUri(GameDomain.From(gameDomain)).ToString();
        actual.Should().Be(expected);
    }

    [Fact]
    public void Test_StaticUris()
    {
        NexusModsUrlBuilder.UserSettingsUri.ToString().Should().Be("https://users.nexusmods.com/");
        NexusModsUrlBuilder.LearnAboutPremiumUri.ToString().Should().Be("https://www.nexusmods.com/premium");
        NexusModsUrlBuilder.UpgradeToPremiumUri.ToString().Should().Be("https://users.nexusmods.com/account/billing/premium");
    }

    [Fact]
    public void Test_NoUriCarriesTrackingParameters()
    {
        var game = GameDomain.From(StardewValley);
        var slug = CollectionSlug.From("tckf0m");
        var revision = Optional<RevisionNumber>.Create(RevisionNumber.From(80));

        var uris = new[]
        {
            NexusModsUrlBuilder.UserSettingsUri,
            NexusModsUrlBuilder.LearnAboutPremiumUri,
            NexusModsUrlBuilder.UpgradeToPremiumUri,
            NexusModsUrlBuilder.GetProfileUri(UserId.From(6672467ul)),
            NexusModsUrlBuilder.GetGameUri(game),
            NexusModsUrlBuilder.GetModUri(game, ModId.From(2400)),
            NexusModsUrlBuilder.GetFileDownloadUri(game, ModId.From(2400), FileId.From(128328), useNxmLink: true),
            NexusModsUrlBuilder.GetFileDownloadUri(game, ModId.From(2400), FileId.From(128328), useNxmLink: false),
            NexusModsUrlBuilder.GetBrowseCollectionsUri(game),
            NexusModsUrlBuilder.GetCollectionUri(game, slug, revision),
            NexusModsUrlBuilder.GetCollectionBugsUri(game, slug, revision),
            NexusModsUrlBuilder.GetCollectionChangelogUri(game, slug, revision),
        };

        foreach (var uri in uris)
        {
            var keys = QueryHelpers.ParseQuery(uri.Query).Keys;
            keys.Should().NotContain(key => key.StartsWith("mtm_", StringComparison.Ordinal), because: uri.ToString());
        }
    }
}
