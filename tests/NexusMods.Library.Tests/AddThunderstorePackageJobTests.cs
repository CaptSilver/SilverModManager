using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Backend;
using NexusMods.Library.Tests.DownloadsService;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.Thunderstore;
using NexusMods.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Thunderstore;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace NexusMods.Library.Tests;

/// <summary>
/// Every test uses its own team name, so the library and the request log, which the tests share, never mix. The
/// real BepInEx packs are the exception: they are published identically wherever they appear.
/// </summary>
public class AddThunderstorePackageJobTests(IServiceProvider serviceProvider)
{
    private static readonly RoutingHandler Routes = new();
    private static readonly RequestHook Hook = new(Routes);

    private sealed class RequestHook(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public Action<Uri>? OnRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            OnRequest?.Invoke(request.RequestUri!);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class LoaderGame(string loader, params string[] aliases) : IThunderstoreGame
    {
        public string ThunderstoreCommunity => "repo";
        public string ThunderstoreLoaderPackage => loader;
        public IReadOnlyList<string> ThunderstoreLoaderAliases => aliases;
    }

    private static ThunderstorePackageRef Package(string team, string name) => new(team, name);
    private static ThunderstoreVersionNumber Number(string text) => ThunderstoreVersionNumber.TryParse(text, out var number) ? number : throw new FormatException(text);
    private static ThunderstoreVersionRef Ref(string team, string name, string version) => new(Package(team, name), Number(version));

    private static byte[] ZipBytes(string team, string name, string version)
        => Enumerable.Range(0, 1500).Select(i => (byte)(i * 31 + team.Length * 7 + name.Length * 3 + version.Length)).ToArray();

    private static Sha256Value Digest(string team, string name, string version) => Sha256Value.From(SHA256.HashData(ZipBytes(team, name, version)));

    private static string DependencyList(string[] dependencies) => string.Join(",", dependencies.Select(d => $"\"{d}\""));

    private static string VersionJson(string team, string name, string version, string[] dependencies) => $$"""
        {
          "namespace": "{{team}}", "name": "{{name}}", "version_number": "{{version}}", "full_name": "{{team}}-{{name}}-{{version}}",
          "description": "Synthetic", "icon": "https://gcdn.thunderstore.io/live/repository/icons/{{team}}-{{name}}-{{version}}.png",
          "dependencies": [{{DependencyList(dependencies)}}],
          "download_url": "https://thunderstore.io/package/download/{{team}}/{{name}}/{{version}}/",
          "downloads": 1, "date_created": "2025-09-01T10:00:00.000000Z", "website_url": "", "is_active": true
        }
        """;

    /// <summary>
    /// Publishes a package: its version record, its zip, and, for the newest version, the package record.
    /// </summary>
    private static void Publish(string team, string name, string version, string[] dependencies, bool latest = true)
    {
        Routes.Add($"https://thunderstore.io/api/experimental/package/{team}/{name}/{version}/", VersionJson(team, name, version, dependencies));
        Routes.Add($"https://thunderstore.io/package/download/{team}/{name}/{version}/", ZipBytes(team, name, version), "application/zip");
        if (!latest) return;

        Routes.Add($"https://thunderstore.io/api/experimental/package/{team}/{name}/", $$"""
            {
              "namespace": "{{team}}", "name": "{{name}}", "full_name": "{{team}}-{{name}}", "owner": "{{team}}",
              "package_url": "https://thunderstore.io/c/repo/p/{{team}}/{{name}}/",
              "date_created": "2025-01-01T00:00:00.000000Z", "date_updated": "2025-09-02T10:00:00.000000Z",
              "rating_score": 1, "is_pinned": false, "is_deprecated": false, "total_downloads": 1,
              "latest": {{VersionJson(team, name, version, dependencies)}},
              "community_listings": [{ "community": "repo", "categories": [], "review_status": "approved", "has_nsfw_content": false, "url": "https://thunderstore.io/c/repo/p/{{team}}/{{name}}/" }]
            }
            """);
    }

    private IConnection Connection => serviceProvider.GetRequiredService<IConnection>();
    private IThunderstoreSource Source => serviceProvider.GetRequiredService<IThunderstoreSource>();

    private static ThunderstoreInstallRequest Request(
        ThunderstoreVersionRef root,
        IThunderstoreGame game,
        bool withDependencies = true,
        DependencyPolicy policy = DependencyPolicy.Latest,
        Sha256Value? expected = null,
        Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber>? installed = null)
        => new(root, game, withDependencies, policy, expected, installed ?? []);

    private static string[] DownloadsOf(string team) => Routes.Requests
        .Where(uri => uri.AbsolutePath.StartsWith($"/package/download/{team}/", StringComparison.Ordinal))
        .Select(uri => uri.AbsolutePath.Trim('/').Replace("package/download/", "").Replace('/', '-'))
        .Distinct() // the downloader probes a URL before it fetches it
        .ToArray();

    private ThunderstoreLibraryFile.ReadOnly Thunderstore(LibraryFile.ReadOnly file) => ThunderstoreLibraryFile.Load(Connection.Db, file.Id);

    private static string Describe(IEnumerable<LibraryFile.ReadOnly> files) => string.Join(",", files.Select(file => file.FileName));

    [Fact]
    public async Task DependenciesAreDownloadedBeforeTheRootAndASecondCallDownloadsNothing()
    {
        const string team = "Zehs";
        Publish("BepInEx", "BepInExPack", "5.4.2305", []);
        Publish(team, "REPOLib", "4.2.0", ["BepInEx-BepInExPack-5.4.2305"]);
        var game = new ThunderstoreStubbedGame(serviceProvider);

        var first = await Source.AddAsync(Request(Ref(team, "REPOLib", "4.2.0"), game), CancellationToken.None);

        first.Select(file => file.FileName).Should().Equal("BepInEx-BepInExPack-5.4.2305.zip", "Zehs-REPOLib-4.2.0.zip");
        Thunderstore(first[0]).PinnedSha256.Should().Be(Digest("BepInEx", "BepInExPack", "5.4.2305"));
        Thunderstore(first[1]).PinnedSha256.Should().Be(Digest(team, "REPOLib", "4.2.0"));

        Routes.Requests.Clear();
        var second = await Source.AddAsync(Request(Ref(team, "REPOLib", "4.2.0"), game), CancellationToken.None);

        second.Select(file => file.Id).Should().Equal(first.Select(file => file.Id));
        Routes.Requests.Where(uri => uri.AbsolutePath.StartsWith("/package/download/", StringComparison.Ordinal)).Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutDependenciesOnlyTheRootIsReturned()
    {
        const string team = "NoDeps";
        Publish(team, "Loader", "1.0.0", []);
        Publish(team, "Mod", "1.0.0", [$"{team}-Loader-1.0.0"]);

        var files = await Source.AddAsync(Request(Ref(team, "Mod", "1.0.0"), new LoaderGame($"{team}-Loader"), withDependencies: false), CancellationToken.None);

        Describe(files).Should().Be($"{team}-Mod-1.0.0.zip");
        DownloadsOf(team).Should().Equal($"{team}-Mod-1.0.0");
    }

    [Fact]
    public async Task LibraryPresenceIsPerVersion()
    {
        const string team = "PerVersion";
        var game = new LoaderGame($"{team}-Loader");
        Publish(team, "Loader", "1.0.0", []);
        Publish(team, "MenuLib", "2.0.0", [], latest: false);
        Publish(team, "MenuLib", "2.5.2", []);
        Publish(team, "Config", "1.0.0", [$"{team}-Loader-1.0.0", $"{team}-MenuLib-2.0.0"]);

        // 2.0.0 is already in the library, but the resolver wants the latest version.
        await Source.AddAsync(Request(Ref(team, "MenuLib", "2.0.0"), game, withDependencies: false), CancellationToken.None);
        Routes.Requests.Clear();

        var files = await Source.AddAsync(Request(Ref(team, "Config", "1.0.0"), game), CancellationToken.None);

        Describe(files).Should().Be($"{team}-Loader-1.0.0.zip,{team}-MenuLib-2.5.2.zip,{team}-Config-1.0.0.zip");
        DownloadsOf(team).Should().Equal($"{team}-Loader-1.0.0", $"{team}-MenuLib-2.5.2", $"{team}-Config-1.0.0");
    }

    [Fact]
    public async Task APackageAlreadyInTheLoadoutIsNotReturned()
    {
        const string team = "InLoadout";
        var game = new LoaderGame($"{team}-Loader");
        Publish(team, "Loader", "1.0.0", []);
        Publish(team, "MenuLib", "2.5.2", []);
        Publish(team, "Config", "1.0.0", [$"{team}-Loader-1.0.0", $"{team}-MenuLib-2.0.0"]);

        var files = await Source.AddAsync(
            Request(Ref(team, "Config", "1.0.0"), game, installed: new() { [Package(team, "MenuLib")] = Number("2.5.2") }),
            CancellationToken.None);

        Describe(files).Should().Be($"{team}-Loader-1.0.0.zip,{team}-Config-1.0.0.zip");
        DownloadsOf(team).Should().NotContain($"{team}-MenuLib-2.5.2");
    }

    [Fact]
    public async Task ADependencyOnALoaderAliasInstallsTheGameLoader()
    {
        const string team = "AliasTeam";
        var peak = new LoaderGame("BepInEx-BepInExPack_PEAK", "BepInEx-BepInExPack", "bbepis-BepInExPack", "xiaoxiao921-BepInExPack");
        Publish("BepInEx", "BepInExPack_PEAK", "5.4.75301", []);
        Publish("BepInEx", "BepInExPack", "5.4.2305", []);
        Publish(team, "Mod", "1.0.0", ["BepInEx-BepInExPack-5.4.2305"]);
        Routes.Requests.Clear();

        var files = await Source.AddAsync(Request(Ref(team, "Mod", "1.0.0"), peak), CancellationToken.None);

        Describe(files).Should().Be($"BepInEx-BepInExPack_PEAK-5.4.75301.zip,{team}-Mod-1.0.0.zip");
        Routes.Requests.Select(uri => uri.AbsolutePath).Should().NotContain(path => path.Contains("/BepInEx/BepInExPack/"));
    }

    [Fact]
    public async Task ExactPolicyTakesTheVersionsTheRootLists()
    {
        const string team = "ExactTeam";
        var game = new LoaderGame($"{team}-Loader");
        Publish(team, "Loader", "1.0.0", []);
        Publish(team, "Lib", "1.0.0", [], latest: false);
        Publish(team, "Lib", "2.0.0", []);
        Publish(team, "Mid", "1.0.0", [$"{team}-Lib-2.0.0"]);
        Publish(team, "Pack", "1.0.0", [$"{team}-Loader-1.0.0", $"{team}-Lib-1.0.0", $"{team}-Mid-1.0.0"]);

        var files = await Source.AddAsync(Request(Ref(team, "Pack", "1.0.0"), game, policy: DependencyPolicy.Exact), CancellationToken.None);

        Describe(files).Should().Be($"{team}-Loader-1.0.0.zip,{team}-Lib-1.0.0.zip,{team}-Mid-1.0.0.zip,{team}-Pack-1.0.0.zip");
        DownloadsOf(team).Should().NotContain($"{team}-Lib-2.0.0");
    }

    [Fact]
    public async Task ARootDigestMismatchFailsAfterTheDependenciesWereAdded()
    {
        const string team = "Mismatch";
        var game = new LoaderGame($"{team}-Loader");
        Publish(team, "Loader", "1.0.0", []);
        Publish(team, "Mod", "1.0.0", [$"{team}-Loader-1.0.0"]);
        var wrong = Sha256Value.From(SHA256.HashData("other"u8));

        var act = async () => await Source.AddAsync(Request(Ref(team, "Mod", "1.0.0"), game, expected: wrong), CancellationToken.None);

        await act.Should().ThrowAsync<ThunderstoreIntegrityException>().WithMessage($"*{team}-Mod-1.0.0*");
        var inLibrary = ThunderstoreLibraryFile.All(Connection.Db)
            .Where(file => file.Namespace == team).Select(file => file.Name).ToArray();
        inLibrary.Should().Equal("Loader");
    }

    [Fact]
    public async Task CancellingWhileADependencyDownloadsStopsTheRun()
    {
        const string team = "Cancelled";
        var game = new LoaderGame($"{team}-Loader");
        Publish(team, "Loader", "1.0.0", []);
        Publish(team, "Mod", "1.0.0", [$"{team}-Loader-1.0.0"]);
        using var cts = new CancellationTokenSource();
        Hook.OnRequest = uri => { if (uri.AbsolutePath.Contains($"/package/download/{team}/Loader/")) cts.Cancel(); };

        try
        {
            var act = async () => await Source.AddAsync(Request(Ref(team, "Mod", "1.0.0"), game), cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            DownloadsOf(team).Should().NotContain($"{team}-Mod-1.0.0");
            ThunderstoreLibraryFile.All(Connection.Db).Where(file => file.Namespace == team).Should().BeEmpty();
        }
        finally
        {
            Hook.OnRequest = null;
        }
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
                .AddSingleton(new HttpClient(Hook));
        }
    }
}
