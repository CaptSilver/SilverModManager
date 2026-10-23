using System.Net;
using FluentAssertions;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using Xunit;

namespace SilverModManager.Networking.NexusWebApi.Tests;

[Trait("RequiresNetworking", "True")]
public class NexusApiClientTests
{
    private readonly NexusApiClient _nexusApiClient;

    public NexusApiClientTests(NexusApiClient nexusApiClient)
    {
        _nexusApiClient = nexusApiClient;
    }

    [Fact]
    [Trait("RequiresApiKey", "True")]
    public async Task CanGetCollectionDownloadLinks()
    {
        ApiKeyTestHelper.RequireApiKey();
        var links = await _nexusApiClient.CollectionDownloadLinksAsync(CollectionSlug.From("iszwwe"), RevisionNumber.From(469));
        links.Data.DownloadLinks.Should().HaveCountGreaterThan(0);
    }
}
