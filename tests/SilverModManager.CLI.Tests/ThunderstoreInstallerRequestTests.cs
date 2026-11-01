using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library;
using SilverModManager.Games.TestFramework;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.DTOs;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.Sdk.Thunderstore;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// What the installer asks the source for, read through a source that records the request and a catalog that says
/// which packages are modpacks.
/// </summary>
[Collection("ConsoleOutput")]
public class ThunderstoreInstallerRequestTests(ITestOutputHelper helper) : AIsolatedGameTest<ThunderstoreInstallerRequestTests, ThunderstoreStubbedGame>(helper)
{
    private readonly ThunderstoreVerbFixtures.RecordingThunderstoreSource _source = new();
    private readonly FakeCatalogSource _catalogs = new();

    private sealed class FakeCatalogSource : IThunderstoreCatalogSource
    {
        public bool Stale { get; set; }
        public int Refreshes { get; private set; }
        public List<string> Loaded { get; } = [];

        public bool IsStale(string community) => Stale;

        public Task Refresh(string community, CancellationToken cancellationToken)
        {
            Refreshes++;
            Stale = false;
            return Task.CompletedTask;
        }

        public Task<ThunderstoreCatalog> Load(string community, CancellationToken cancellationToken)
        {
            Loaded.Add(community);
            return Task.FromResult(new ThunderstoreCatalog(community, [Listing("Zehs", "REPOLib", "Mods"), Listing("Packs", "BigPack", "Modpacks")]));
        }

        private static PackageListing Listing(string team, string name, string category) => new()
        {
            Name = name,
            FullName = $"{team}-{name}",
            Owner = team,
            Categories = [category],
            Versions = [new PackageVersion { Name = name, FullName = $"{team}-{name}-1.0.0", VersionNumber = "1.0.0", DownloadUrl = $"https://thunderstore.io/package/download/{team}/{name}/1.0.0/" }],
        };
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<ThunderstoreStubbedGame>()
            .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(ThunderstoreVerbFixtures.BuildRoutes());
        services.Replace(ServiceDescriptor.Singleton<IThunderstoreSource>(_source));
        services.Replace(ServiceDescriptor.Singleton<IThunderstoreCatalogSource>(_catalogs));
        return services;
    }

    private IThunderstoreInstaller Installer => ServiceProvider.GetRequiredService<IThunderstoreInstaller>();

    private static ThunderstoreVersionRef Ref(string team, string name) => new(new ThunderstorePackageRef(team, name), new ThunderstoreVersionNumber(1, 0, 0));

    [Fact]
    public async Task AnOrdinaryPackageTakesTheLatestDependencies()
    {
        var loadout = await CreateLoadout();

        await Installer.InstallAsync(Ref("Zehs", "REPOLib"), loadout.LoadoutId, CancellationToken.None);

        var request = _source.Requests.Should().ContainSingle().Subject;
        request.Policy.Should().Be(DependencyPolicy.Latest);
        request.WithDependencies.Should().BeTrue();
        request.Root.Should().Be(Ref("Zehs", "REPOLib"));
        request.Game.ThunderstoreCommunity.Should().Be("repo");
        _catalogs.Loaded.Should().Equal("repo");
    }

    [Fact]
    public async Task AModpackTakesTheDependencyVersionsItLists()
    {
        var loadout = await CreateLoadout();

        await Installer.InstallAsync(Ref("Packs", "BigPack"), loadout.LoadoutId, CancellationToken.None);

        _source.Requests.Should().ContainSingle().Which.Policy.Should().Be(DependencyPolicy.Exact);
    }

    [Theory]
    [InlineData(DependencyPolicy.Exact)]
    [InlineData(DependencyPolicy.Latest)]
    public async Task AnExplicitPolicyWinsOverTheModpackRule(DependencyPolicy policy)
    {
        var loadout = await CreateLoadout();

        await Installer.InstallAsync(Ref("Zehs", "REPOLib"), loadout.LoadoutId, policy, CancellationToken.None);

        _source.Requests.Should().ContainSingle().Which.Policy.Should().Be(policy);
        _catalogs.Loaded.Should().BeEmpty("an explicit policy needs no catalog");
    }

    [Fact]
    public async Task AStaleCatalogIsRefreshedBeforeItIsRead()
    {
        var loadout = await CreateLoadout();
        _catalogs.Stale = true;

        await Installer.InstallAsync(Ref("Packs", "BigPack"), loadout.LoadoutId, CancellationToken.None);

        _catalogs.Refreshes.Should().Be(1);
        _source.Requests.Single().Policy.Should().Be(DependencyPolicy.Exact);
    }

    [Fact]
    public async Task ASourceFailurePropagatesWithItsMessage()
    {
        var loadout = await CreateLoadout();
        _source.Failure = new DependencyResolutionException("Zehs-REPOLib needs Missing-Thing, which is not in the index.");

        var act = () => Installer.InstallAsync(Ref("Zehs", "REPOLib"), loadout.LoadoutId, CancellationToken.None).AsTask();

        (await act.Should().ThrowAsync<DependencyResolutionException>()).WithMessage("*Missing-Thing*");
    }
}
