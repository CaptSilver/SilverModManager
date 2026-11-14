using SilverModManager.Abstractions.Games;
using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GitHub;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.Sdk.Library;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The <c>github</c> verbs over a stubbed game that lists one loader on its shelf, with GitHub answered by a substitute
/// and release assets served from memory.
/// </summary>
[Collection("ConsoleOutput")]
public class GitHubVerbsTests(ITestOutputHelper helper) : AIsolatedGameTest<GitHubVerbsTests, GitHubShelfStubbedGame>(helper)
{
    private const string AssetUrl = "https://example.invalid/tool_v3.zip";

    // A real archive, because the install step unpacks it; the content is unique so the library keeps a file of its own.
    private readonly byte[] _payload = ZipOf("loader.dll", Guid.NewGuid().ToByteArray());
    private readonly IGitHubApi _api = Substitute.For<IGitHubApi>();
    private readonly Dictionary<string, Release?> _latest = new(StringComparer.OrdinalIgnoreCase);

    private static byte[] ZipOf(string name, byte[] content)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(content);
        }

        return stream.ToArray();
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri!.ToString() == AssetUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static Release ReleaseOf(string tag, string repository, params string[] assets) => new(
        "u", "u", $"https://github.com/o/{repository}/releases/tag/{tag}", 1, tag, "main", tag, false, false, default, default,
        [.. assets.Select(name => new Asset("u", 1, name, "application/octet-stream", "uploaded", 10, 0, default, default, "https://example.invalid/" + name))],
        "u", "u", "Notes");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        _latest["o/tool"] = ReleaseOf("v3", "tool", "tool_v3.zip");
        _api.FetchMarkedLatestRelease(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(_latest.GetValueOrDefault($"{call.ArgAt<string>(0)}/{call.ArgAt<string>(1)}")));
        _api.FetchReleaseByTag(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(_latest.GetValueOrDefault($"{call.ArgAt<string>(0)}/{call.ArgAt<string>(1)}")));

        base.AddServices(services)
            .AddGame<GitHubShelfStubbedGame>()
            .AddUniversalGameLocator<GitHubShelfStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(ThunderstoreVerbFixtures.BuildRoutes());
        services.Replace(ServiceDescriptor.Singleton(new HttpClient(new BytesHandler(_payload))));
        services.Replace(ServiceDescriptor.Singleton(_api));
        return services;
    }

    private Task<(int ExitCode, string Output)> Run(params string[] args) => NexusVerbHost.Run(ServiceProvider, args);

    [Fact]
    public async Task ShelfListsEachEntryWithItsLatestTag()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("github", "shelf", "-l", loadout.Name);

        code.Should().Be(0, output);
        output.Should().Contain("o/tool").And.Contain("v3").And.Contain("Mod loader");
    }

    [Fact]
    public async Task ShelfNamesTheResetTimeWhenGitHubHasNoRequestsLeft()
    {
        var loadout = await CreateLoadout();
        _api.FetchMarkedLatestRelease(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<Release?>>(_ => throw new GitHubRateLimitedException(DateTimeOffset.UtcNow.AddMinutes(30)));

        var (code, output) = await Run("github", "shelf", "-l", loadout.Name);

        code.Should().Be(1, output);
        output.Should().Contain("request limit");
    }

    [Fact]
    public async Task InstallWithoutAnAssetSuffixExitsOneNamingTheOption()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("github", "install", "-l", loadout.Name, "-r", "someone/else");

        code.Should().Be(1, output);
        output.Should().Contain("--asset");
    }

    [Fact]
    public async Task InstallOfAShelfRepositoryTakesItsAssetFromTheShelf()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("github", "install", "-l", loadout.Name, "-r", "o/tool");

        code.Should().Be(0, output);
        output.Should().Contain("tool v3");
        var downloaded = GitHubReleaseLibraryFile.All(Connection.Db).Single(file => file.AssetName == "tool_v3.zip");
        downloaded.PinnedSha256.ToString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(_payload)));
        downloaded.Tag.Should().Be("v3");
    }

    [Fact]
    public async Task InstallOfARepositoryOffTheShelfPutsItOnTheShelf()
    {
        var loadout = await CreateLoadout();
        _latest["someone/else"] = ReleaseOf("v1", "else", "tool_v3.zip");

        var (code, output) = await Run("github", "install", "-l", loadout.Name, "-r", "https://github.com/someone/else/releases", "-a", ".zip");

        code.Should().Be(0, output);
        var row = GitHubShelfEntry.All(Connection.Db).Single(entry => entry.Organization == "someone");
        row.Repository.Should().Be("else", "an installed repository is checked for updates, which needs it on the shelf");
        row.GameId.Should().Be(GitHubShelfStubbedGame.GameId);
    }

    [Fact]
    public async Task AddRecordsAnEntryForTheLoadoutsGame()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("github", "add", "-l", loadout.Name, "-r", "https://github.com/someone/loader/releases", "-a", ".zip", "--hint", "loader_");

        code.Should().Be(0, output);
        output.Should().Contain("someone/loader").And.Contain("Nobody but its owner has checked it");
        var row = GitHubShelfEntry.All(Connection.Db).Single(entry => entry.Organization == "someone");
        row.Repository.Should().Be("loader");
        row.AssetSuffix.Should().Be(".zip");
        row.NameHint.Value.Should().Be("loader_");
        row.GameId.Should().Be(GitHubShelfStubbedGame.GameId);
    }

    [Fact]
    public async Task AddRefusesTextThatIsNotARepository()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("github", "add", "-l", loadout.Name, "-r", "not a repository", "-a", ".zip");

        code.Should().Be(1, output);
        output.Should().Contain("not a repository");
        GitHubShelfEntry.All(Connection.Db).Should().BeEmpty();
    }
}
