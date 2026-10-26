using System.Net;
using FluentAssertions;

namespace SilverModManager.Networking.NexusWebApi.Tests;

public class ArchiveContentPreviewTests
{
    private const string Listing = """
        {"path":"","name":"","type":"directory","size":"3000","children":[
          {"path":"mods","name":"mods","type":"directory","size":"3000","children":[
            {"path":"mods/NoWeight.dll","name":"NoWeight.dll","type":"file","size":"2048"},
            {"path":"mods/readme.txt","name":"readme.txt","type":"file","size":952}
          ]},
          {"path":"top.ini","name":"top.ini","type":"file","size":"1"}
        ]}
        """;

    [Fact]
    public void FilesAreListedWithTheirFolderAndSize()
    {
        ArchiveContentPreview.Parse(Listing).Should().Equal(
            new ArchiveContentEntry("mods/NoWeight.dll", 2048),
            new ArchiveContentEntry("mods/readme.txt", 952),
            new ArchiveContentEntry("top.ini", 1));
    }

    [Fact]
    public void ADocumentWithoutChildrenListsNothing()
    {
        ArchiveContentPreview.Parse("""{"path":"","name":"","type":"directory"}""").Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void ADocumentThatIsNotAListingIsRefused(string json)
    {
        var act = () => ArchiveContentPreview.Parse(json);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ANodeWithoutAUsableSizeIsSkippedNotGuessed()
    {
        var entries = ArchiveContentPreview.Parse("""{"children":[{"name":"a","type":"file","size":"x"},{"name":"b","type":"file","size":"5"}]}""");
        entries.Should().Equal(new ArchiveContentEntry("b", 5));
    }

    [Theory]
    [InlineData("https://file-metadata.nexusmods.com/file/nexus-files-s3-meta/4333/117/Mod-117.zip.json", true)]
    [InlineData("http://file-metadata.nexusmods.com/x.json", false)]
    [InlineData("https://evil.example.com/x.json", false)]
    [InlineData("https://nexusmods.com.evil.example.com/x.json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAnHttpsLinkOnNexusModsIsFollowed(string? link, bool trusted)
    {
        ArchiveContentPreview.IsTrustedLink(link).Should().Be(trusted);
    }

    [Fact]
    public async Task FetchReadsTheListingFromATrustedLink()
    {
        using var client = new HttpClient(new Canned(HttpStatusCode.OK, Listing));

        var entries = await ArchiveContentPreview.FetchAsync(client, "https://file-metadata.nexusmods.com/a.json", default);

        entries.Should().HaveCount(3);
    }

    [Fact]
    public async Task FetchRefusesAnUntrustedLinkWithoutSendingARequest()
    {
        var handler = new Canned(HttpStatusCode.OK, Listing);
        using var client = new HttpClient(handler);

        var act = () => ArchiveContentPreview.FetchAsync(client, "https://evil.example.com/a.json", default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().Be(0);
    }

    [Fact]
    public async Task FetchReportsAFailedResponse()
    {
        using var client = new HttpClient(new Canned(HttpStatusCode.NotFound, ""));

        var act = () => ArchiveContentPreview.FetchAsync(client, "https://file-metadata.nexusmods.com/a.json", default);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private sealed class Canned(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
