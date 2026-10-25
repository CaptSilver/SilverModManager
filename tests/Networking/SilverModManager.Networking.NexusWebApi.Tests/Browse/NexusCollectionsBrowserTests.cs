using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Networking.NexusWebApi.Errors;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi.Tests.Browse;

public class NexusCollectionsBrowserTests
{
    private static readonly NexusModsGameId Game = NexusModsGameId.From(3333);

    private static IQueryCollections_CollectionsV2_Nodes Node(string slug, string name, int? revision = 7, string? tile = "https://img.example/tile.png", string? header = "https://img.example/header.png", bool adult = false)
    {
        var node = Substitute.For<IQueryCollections_CollectionsV2_Nodes>();
        node.Slug.Returns(slug);
        node.Name.Returns(name);
        node.Summary.Returns($"{name} summary");
        node.Endorsements.Returns(12);
        node.TotalDownloads.Returns(3400);
        node.UpdatedAt.Returns(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
        node.User.Name.Returns("curator");
        node.Game.DomainName.Returns("cyberpunk2077");
        node.TileImage!.ThumbnailUrl.Returns(tile ?? "");
        node.HeaderImage!.Url.Returns(header ?? "");
        if (revision is null)
        {
            node.LatestPublishedRevision.Returns((IQueryCollections_CollectionsV2_Nodes_LatestPublishedRevision?)null);
        }
        else
        {
            node.LatestPublishedRevision!.RevisionNumber.Returns(revision.Value);
            node.LatestPublishedRevision!.ModCount.Returns(45);
            node.LatestPublishedRevision!.TotalSize.Returns("123456789");
            node.LatestPublishedRevision!.AdultContent.Returns(adult);
        }
        return node;
    }

    private static IGraphQlClient ClientReturning(int total, params IQueryCollections_CollectionsV2_Nodes[] nodes)
    {
        var page = Substitute.For<IQueryCollections_CollectionsV2>();
        page.TotalCount.Returns(total);
        page.Nodes.Returns(nodes);
        var client = Substitute.For<IGraphQlClient>();
        client.QueryCollections(default!, default!, default, default, default)
            .ReturnsForAnyArgs(_ => ValueTask.FromResult(new GraphQlResult<IQueryCollections_CollectionsV2>(page)));
        return client;
    }

    private static NexusCollectionsBrowser Browser(IGraphQlClient client) => new(client);

    [Fact]
    public async Task Search_SendsTheQueryAsFilterSortOffsetAndCount()
    {
        var client = ClientReturning(0);

        await Browser(client).Search(new NexusCollectionsSearchQuery(Game, "cyber", NexusCollectionsSort.Downloads, 80, 40), CancellationToken.None);

        await client.Received(1).QueryCollections(
            Arg.Is<CollectionsSearchFilter>(f => f.GameId![0].Value == "3333" && f.Name![0].Value == "cyber"),
            Arg.Is<IReadOnlyList<CollectionsSearchSort>>(s => s.Count == 1 && s[0].Downloads != null),
            80,
            40,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_MapsTheNodesToHits()
    {
        var client = ClientReturning(2, Node("abc123", "Alpha"), Node("def456", "Beta", revision: null, tile: null, header: "not a url"));

        var page = await Browser(client).Search(new NexusCollectionsSearchQuery(Game, "", NexusCollectionsSort.Endorsements, 0, 40), CancellationToken.None);

        page.Hits.Select(h => h.Name).Should().Equal("Alpha", "Beta");
        var alpha = page.Hits[0];
        alpha.Slug.Should().Be(CollectionSlug.From("abc123"));
        alpha.GameDomain.Should().Be(GameDomain.From("cyberpunk2077"));
        alpha.Summary.Should().Be("Alpha summary");
        alpha.Author.Should().Be("curator");
        alpha.Endorsements.Should().Be(12);
        alpha.Downloads.Should().Be(3400);
        alpha.ModCount.Should().Be(45);
        alpha.TotalSizeBytes.Should().Be(123456789UL);
        alpha.LatestRevision.Should().Be(RevisionNumber.From(7));
        alpha.UpdatedAt.Should().Be(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
        alpha.ImageUrl.Should().Be(new Uri("https://img.example/tile.png"));
        alpha.AdultContent.Should().BeFalse();

        var beta = page.Hits[1];
        beta.LatestRevision.Should().BeNull("a collection nobody has published has nothing to install");
        beta.ModCount.Should().Be(0);
        beta.ImageUrl.Should().BeNull("neither image is an absolute URL");
    }

    [Fact]
    public async Task Search_FallsBackToTheHeaderImageWithoutATile()
    {
        var client = ClientReturning(1, Node("abc123", "Alpha", tile: null));

        var page = await Browser(client).Search(new NexusCollectionsSearchQuery(Game, "", NexusCollectionsSort.Endorsements, 0, 40), CancellationToken.None);

        page.Hits[0].ImageUrl.Should().Be(new Uri("https://img.example/header.png"));
    }

    [Fact]
    public async Task Search_ReportsMorePagesWhileHitsRemain()
    {
        var first = await Browser(ClientReturning(3, Node("a", "A"), Node("b", "B")))
            .Search(new NexusCollectionsSearchQuery(Game, "", NexusCollectionsSort.Updated, 0, 2), CancellationToken.None);
        var last = await Browser(ClientReturning(3, Node("c", "C")))
            .Search(new NexusCollectionsSearchQuery(Game, "", NexusCollectionsSort.Updated, 2, 2), CancellationToken.None);

        first.HasMore.Should().BeTrue();
        last.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Search_ThrowsWhenTheApiReturnsNoData()
    {
        var client = Substitute.For<IGraphQlClient>();
        client.QueryCollections(default!, default!, default, default, default)
            .ReturnsForAnyArgs(_ => ValueTask.FromResult(new GraphQlResult<IQueryCollections_CollectionsV2>([])));

        var act = async () => await Browser(client).Search(new NexusCollectionsSearchQuery(Game, "", NexusCollectionsSort.Updated, 0, 40), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
