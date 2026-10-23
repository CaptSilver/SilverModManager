using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend;
using NexusMods.Library.Tests.DownloadsService;
using NexusMods.Networking.Thunderstore;
using NexusMods.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Thunderstore;
using Xunit;

namespace NexusMods.Library.Tests;

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
