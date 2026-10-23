using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Thunderstore;
using Xunit;

namespace SilverModManager.Library.Tests;

/// <summary>
/// Runs the job through the overload the app uses: the registered <see cref="IThunderstoreApi"/> and
/// <see cref="HttpClient"/>, answering from the experimental version JSON Thunderstore serves.
/// </summary>
public class ThunderstoreDownloadJobApiTests(IServiceProvider serviceProvider)
{
    private const string VersionUrl = "https://thunderstore.io/api/experimental/package/Zehs/REPOLib/4.2.0/";
    private const string DownloadUrl = "https://thunderstore.io/package/download/Zehs/REPOLib/4.2.0/";
    private static readonly byte[] Payload = Enumerable.Range(0, 2048).Select(i => (byte)(i * 13 + 5)).ToArray();

    private static ThunderstoreVersionRef Ref(string name) => new(new ThunderstorePackageRef("Zehs", name), new ThunderstoreVersionNumber(4, 2, 0));

    [Fact]
    public async Task TheRegisteredApiAndClientDownloadAndRecordTheVersion()
    {
        var result = await ThunderstoreDownloadJob.Create(serviceProvider, Ref("REPOLib"), "repo");

        result.Namespace.Should().Be("Zehs");
        result.Name.Should().Be("REPOLib");
        result.VersionNumber.Should().Be("4.2.0");
        result.Community.Should().Be("repo");
        result.PinnedSha256.Should().Be(Sha256Value.From(SHA256.HashData(Payload)));
        result.AsLibraryFile().FileName.Should().Be("Zehs-REPOLib-4.2.0.zip");
    }

    [Fact]
    public async Task AVersionTheApiDoesNotKnowFailsNamingIt()
    {
        var act = async () => await ThunderstoreDownloadJob.Create(serviceProvider, Ref("Missing"), "repo");

        await act.Should().ThrowAsync<ThunderstoreIntegrityException>().WithMessage("*Zehs-Missing-4.2.0*");
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            var handler = new RoutingHandler()
                .Add(VersionUrl, ThunderstoreJson.ExperimentalVersion)
                .Add(DownloadUrl, Payload, "application/zip");

            services
                .AddLibraryModels()
                .AddOSInterop()
                .AddFileExtractors()
                .AddThunderstore()
                .AddSingleton(new HttpClient(handler));
        }
    }
}
