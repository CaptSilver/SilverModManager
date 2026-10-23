using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Backend;
using NexusMods.Library.Lock;
using NexusMods.Library.Sources;
using NexusMods.Library.Tests.DownloadsService;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.Thunderstore;
using NexusMods.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace NexusMods.Library.Tests.Lock;

/// <summary>
/// The real Thunderstore lock source against canned Thunderstore responses. Each test uses its own team names so the
/// shared library and request log never mix.
/// </summary>
public class ThunderstoreLibrarySourceTests(IServiceProvider serviceProvider)
{
    private static readonly RoutingHandler Routes = new();

    private static byte[] ZipBytes(string seed) => Enumerable.Range(0, 1200).Select(i => (byte)(i * 13 + seed.Length)).ToArray();

    private static Sha256Value DigestOf(string seed) => Sha256Value.From(SHA256.HashData(ZipBytes(seed)));

    private static string VersionJson(string team, string name, string version) => $$"""
        {
          "namespace": "{{team}}", "name": "{{name}}", "version_number": "{{version}}", "full_name": "{{team}}-{{name}}-{{version}}",
          "description": "Synthetic", "icon": "https://gcdn.thunderstore.io/live/repository/icons/{{team}}-{{name}}-{{version}}.png",
          "dependencies": [],
          "download_url": "https://thunderstore.io/package/download/{{team}}/{{name}}/{{version}}/",
          "downloads": 1, "date_created": "2025-09-01T10:00:00.000000Z", "website_url": "", "is_active": true
        }
        """;

    /// <summary>
    /// Publishes a package whose zip is <see cref="ZipBytes"/> of its full name.
    /// </summary>
    private static void Publish(string team, string name, string version)
    {
        Routes.Add($"https://thunderstore.io/api/experimental/package/{team}/{name}/{version}/", VersionJson(team, name, version));
        Routes.Add($"https://thunderstore.io/package/download/{team}/{name}/{version}/", ZipBytes($"{team}-{name}-{version}"), "application/zip");
        Routes.Add($"https://thunderstore.io/api/experimental/package/{team}/{name}/", $$"""
            {
              "namespace": "{{team}}", "name": "{{name}}", "full_name": "{{team}}-{{name}}", "owner": "{{team}}",
              "package_url": "https://thunderstore.io/c/repo/p/{{team}}/{{name}}/",
              "date_created": "2025-01-01T00:00:00.000000Z", "date_updated": "2025-09-02T10:00:00.000000Z",
              "rating_score": 1, "is_pinned": false, "is_deprecated": false, "total_downloads": 1,
              "latest": {{VersionJson(team, name, version)}},
              "community_listings": [{ "community": "repo", "categories": [], "review_status": "approved", "has_nsfw_content": false, "url": "https://thunderstore.io/c/repo/p/{{team}}/{{name}}/" }]
            }
            """);
    }

    private IConnection Connection => serviceProvider.GetRequiredService<IConnection>();

    private ILibrarySource Source => serviceProvider.GetServices<ILibrarySource>().OfType<ThunderstoreLibrarySource>().Single();

    private LibrarySourceContext Context => new(Connection, new ThunderstoreStubbedGame(serviceProvider), null);

    private static LibrarySourceEntry Entry(string asset, Sha256Value digest) => new("pkg", "1.0.0", null, asset, digest.ToString(), LockSources.Thunderstore);

    [Fact]
    public async Task ATeamWithAHyphenIsFoundByTryingEverySplitOfTheArchiveName()
    {
        Publish("Legacy-Team", "Mod", "1.0.0");
        var digest = DigestOf("Legacy-Team-Mod-1.0.0");

        var file = await Source.Download(Entry("Legacy-Team-Mod-1.0.0.zip", digest), digest, Context, TestContext.Current.CancellationToken);

        file.FileName.ToString().Should().Be("Legacy-Team-Mod-1.0.0.zip");
        file.Sha256.Value.Should().Be(digest);
        var package = ThunderstoreLibraryFile.Load(Connection.Db, file.Id);
        (package.Namespace, package.Name, package.VersionNumber).Should().Be(("Legacy-Team", "Mod", "1.0.0"));
    }

    [Fact]
    public async Task AnArchiveNameThatSplitsTwoWaysAndExistsBothWaysFailsNamingTheAsset()
    {
        Publish("Both", "Team-Mod", "1.0.0");
        Publish("Both-Team", "Mod", "1.0.0");
        var digest = DigestOf("Both-Team-Mod-1.0.0");

        var act = async () => await Source.Download(Entry("Both-Team-Mod-1.0.0.zip", digest), digest, Context, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Both-Team-Mod-1.0.0.zip").And.Contain("more than one");
    }

    [Fact]
    public async Task AnArchiveNameThatMatchesNoPackageFailsNamingTheAsset()
    {
        var digest = DigestOf("Nobody-Nothing-1.0.0");

        var act = async () => await Source.Download(Entry("Nobody-Nothing-1.0.0.zip", digest), digest, Context, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Nobody-Nothing-1.0.0.zip").And.Contain("no package");
    }

    [Fact]
    public async Task ADifferentCopyOfTheSameVersionAlreadyInTheLibraryIsRefusedAndLeftAlone()
    {
        Publish("Clash", "Mod", "1.0.0");
        var existing = await new LockFixtures(Connection).AddThunderstoreFile("Clash", "Mod", "1.0.0", "other bytes", LockFixtures.DigestOf("other bytes"));
        var digest = DigestOf("Clash-Mod-1.0.0");

        var act = async () => await Source.Download(Entry("Clash-Mod-1.0.0.zip", digest), digest, Context, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Clash-Mod-1.0.0.zip").And.Contain("different");
        LibraryFile.Load(Connection.Db, existing.Id).IsValid().Should().BeTrue();
    }

    [Fact]
    public void AGameWithoutThunderstoreSupportRefusesBeforeAnyRequest()
    {
        var digest = DigestOf("Zehs-REPOLib-1.0.0");
        var context = new LibrarySourceContext(Connection, new StubbedGame(serviceProvider), null);

        Source.WhyNotDownloadable(Entry("Zehs-REPOLib-1.0.0.zip", digest), context).Should().Contain("Stubbed Game");
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services
                .AddLibraryModels()
                .AddOSInterop()
                .AddFileExtractors()
                .AddThunderstore()
                .AddSingleton<IThunderstoreSource, ThunderstoreSource>()
                .AddSingleton<ILibrarySource, ThunderstoreLibrarySource>()
                .AddSingleton(new HttpClient(Routes));
        }
    }
}
