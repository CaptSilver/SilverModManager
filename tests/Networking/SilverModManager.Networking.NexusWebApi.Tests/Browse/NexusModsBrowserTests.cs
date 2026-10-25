using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Networking.NexusWebApi.Errors;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi.Tests.Browse;

public class NexusModsBrowserTests
{
    private static readonly NexusModsGameId Game = NexusModsGameId.From(3333);

    private static IQueryMods_Mods_Nodes Node(int modId, string name, string? author = "someone", string? thumb = "https://img.example/t.png", string? picture = "https://img.example/p.png", bool? adult = false)
    {
        var node = Substitute.For<IQueryMods_Mods_Nodes>();
        node.ModId.Returns(modId);
        node.Uid.Returns($"uid-{modId}");
        node.Name.Returns(name);
        node.Summary.Returns($"{name} summary");
        node.Author.Returns(author);
        node.Uploader.Name.Returns("uploader");
        node.Version.Returns("1.2");
        node.Endorsements.Returns(10);
        node.Downloads.Returns(200);
        node.UpdatedAt.Returns(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
        node.ThumbnailUrl.Returns(thumb);
        node.PictureUrl.Returns(picture);
        node.AdultContent.Returns(adult);
        return node;
    }

    private static IGraphQlClient ClientReturning(int total, params IQueryMods_Mods_Nodes[] nodes)
    {
        var mods = Substitute.For<IQueryMods_Mods>();
        mods.TotalCount.Returns(total);
        mods.Nodes.Returns(nodes);
        var client = Substitute.For<IGraphQlClient>();
        client.QueryMods(default!, default!, default, default, default)
            .ReturnsForAnyArgs(_ => ValueTask.FromResult(new GraphQlResult<IQueryMods_Mods>(mods)));
        return client;
    }

    private static NexusModsBrowser Browser(IGraphQlClient client)
        => new(client, null!, NullLogger<NexusModsBrowser>.Instance);

    [Fact]
    public async Task Search_SendsTheQueryAsFilterSortOffsetAndCount()
    {
        var client = ClientReturning(0);

        await Browser(client).Search(new NexusModsSearchQuery(Game, "cyber", NexusModsSort.Downloads, 80, 40), CancellationToken.None);

        await client.Received(1).QueryMods(
            Arg.Is<ModsFilter>(f => f.GameId![0].Value == "3333" && f.Name![0].Value == "cyber"),
            Arg.Is<IReadOnlyList<ModsSort>>(s => s.Count == 1 && s[0].Downloads != null),
            80,
            40,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_MapsTheNodesToHits()
    {
        var client = ClientReturning(2, Node(5, "Alpha"), Node(6, "Beta", author: null, thumb: null, picture: "not a url"));

        var page = await Browser(client).Search(new NexusModsSearchQuery(Game, "", NexusModsSort.Endorsements, 0, 40), CancellationToken.None);

        page.Hits.Select(h => h.Name).Should().Equal("Alpha", "Beta");
        var alpha = page.Hits[0];
        alpha.ModId.Should().Be(ModId.From(5));
        alpha.Summary.Should().Be("Alpha summary");
        alpha.Author.Should().Be("someone");
        alpha.Version.Should().Be("1.2");
        alpha.Endorsements.Should().Be(10);
        alpha.Downloads.Should().Be(200);
        alpha.UpdatedAt.Should().Be(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
        alpha.ThumbnailUrl.Should().Be(new Uri("https://img.example/t.png"));
        alpha.PictureUrl.Should().Be(new Uri("https://img.example/p.png"));
        alpha.AdultContent.Should().BeFalse();

        var beta = page.Hits[1];
        beta.Author.Should().Be("uploader", "the uploader's name stands in when the mod has no author credit");
        beta.ThumbnailUrl.Should().BeNull();
        beta.PictureUrl.Should().BeNull("a value that is not an absolute URL is dropped");
    }

    [Fact]
    public async Task Search_ReportsMorePagesWhileHitsRemain()
    {
        var first = await Browser(ClientReturning(3, Node(1, "A"), Node(2, "B")))
            .Search(new NexusModsSearchQuery(Game, "", NexusModsSort.Name, 0, 2), CancellationToken.None);
        var last = await Browser(ClientReturning(3, Node(3, "C")))
            .Search(new NexusModsSearchQuery(Game, "", NexusModsSort.Name, 2, 2), CancellationToken.None);

        first.HasMore.Should().BeTrue();
        last.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Search_ThrowsWhenTheApiReturnsNoData()
    {
        var client = Substitute.For<IGraphQlClient>();
        client.QueryMods(default!, default!, default, default, default)
            .ReturnsForAnyArgs(_ => ValueTask.FromResult(new GraphQlResult<IQueryMods_Mods>([])));

        var act = async () => await Browser(client).Search(new NexusModsSearchQuery(Game, "", NexusModsSort.Name, 0, 40), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static IQueryModFileCategories_ModFiles FileOf(int id, ModFileCategory category)
    {
        var file = Substitute.For<IQueryModFileCategories_ModFiles>();
        file.FileId.Returns(id);
        file.Category.Returns(category);
        return file;
    }

    [Fact]
    public async Task FindMainFile_ReturnsTheOnlyMainFileOfTheMod()
    {
        var files = new[] { FileOf(1, ModFileCategory.Optional), FileOf(2, ModFileCategory.Main) };
        var client = Substitute.For<IGraphQlClient>();
        client.QueryModFileCategories(ModId.From(9), Game, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new GraphQlResult<IQueryModFileCategories_ModFiles[], NotFound>(files)));

        (await Browser(client).FindMainFile(Game, ModId.From(9), CancellationToken.None)).Should().Be(FileId.From(2));
    }

    [Fact]
    public async Task FindMainFile_IsNullWhenTheModIsUnknown()
    {
        var client = Substitute.For<IGraphQlClient>();
        client.QueryModFileCategories(default, default, default)
            .ReturnsForAnyArgs(ValueTask.FromResult(new GraphQlResult<IQueryModFileCategories_ModFiles[], NotFound>(new NotFound { Message = "no such mod" })));

        (await Browser(client).FindMainFile(Game, ModId.From(9), CancellationToken.None)).Should().BeNull();
    }
}
