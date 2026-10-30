using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi.Tests;

public class NexusApiClientResponseTests
{
    private static NexusApiClient ClientAnswering(string json)
    {
        var factory = Substitute.For<IHttpMessageFactory>();
        factory.Create(Arg.Any<HttpMethod>(), Arg.Any<Uri>())
            .Returns(call => ValueTask.FromResult(new HttpRequestMessage(call.Arg<HttpMethod>(), call.Arg<Uri>())));

        var handler = Substitute.ForPartsOf<MockHttpMessageHandler>();
        handler.SendMock(Arg.Any<HttpRequestMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));

        return new NexusApiClient(NullLogger<NexusApiClient>.Instance, factory, new HttpClient(handler), Substitute.For<IGraphQlClient>());
    }

    [Fact]
    public async Task Test_PayloadMissingARequiredFieldIsAReadableRefusalNamingRequestAndField()
    {
        var client = ClientAnswering("""{"files": [{"name": "Main"}]}""");

        var thrown = await Assert.ThrowsAnyAsync<UserFacingException>(() => client.ModFilesAsync("skyrim", ModId.From(12), TestContext.Current.CancellationToken));

        thrown.Message.Should().Contain("skyrim/mods/12/files.json").And.Contain("file_id");
    }

    [Fact]
    public async Task Test_ArrayPayloadWithANullRequiredFieldIsAReadableRefusal()
    {
        var client = ClientAnswering("""[{"name": "CDN", "short_name": "CDN", "URI": null}]""");

        var thrown = await Assert.ThrowsAnyAsync<UserFacingException>(() => client.DownloadLinksAsync("skyrim", ModId.From(12), FileId.From(7), TestContext.Current.CancellationToken));

        thrown.Message.Should().Contain("download_link.json").And.Contain("URI");
    }
}
