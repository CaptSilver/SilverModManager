using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Thunderstore;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// What <c>thunderstore install</c> asks the source for, observed through a source that records the request and
/// downloads nothing.
/// </summary>
[Collection("ConsoleOutput")]
public class ThunderstoreVerbSourceTests(ITestOutputHelper helper) : AIsolatedGameTest<ThunderstoreVerbSourceTests, ThunderstoreStubbedGame>(helper)
{
    private readonly RoutingHandler _routes = ThunderstoreVerbFixtures.BuildRoutes();
    private readonly RecordingSource _source = new();

    private sealed class RecordingSource : IThunderstoreSource
    {
        public List<ThunderstoreInstallRequest> Requests { get; } = [];
        public Exception? Failure { get; set; }

        public ValueTask<IReadOnlyList<LibraryFile.ReadOnly>> AddAsync(ThunderstoreInstallRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Failure is not null) throw Failure;
            return ValueTask.FromResult<IReadOnlyList<LibraryFile.ReadOnly>>([]);
        }
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<ThunderstoreStubbedGame>()
            .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(_routes);
        services.Replace(ServiceDescriptor.Singleton<IThunderstoreSource>(_source));
        return services;
    }

    private Task<(int ExitCode, string Output)> Run(params string[] args) => ThunderstoreVerbFixtures.Run(ServiceProvider, args);

    [Theory]
    [InlineData(false, DependencyPolicy.Latest)]
    [InlineData(true, DependencyPolicy.Exact)]
    public async Task ExactReachesTheSourceAsTheExactPolicy(bool exact, DependencyPolicy expected)
    {
        var loadout = await CreateLoadout();
        var args = new List<string> { "thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib" };
        if (exact) args.Add("--exact");

        var (code, output) = await Run(args.ToArray());

        code.Should().Be(0, output);
        var request = _source.Requests.Should().ContainSingle().Subject;
        request.Policy.Should().Be(expected);
        request.WithDependencies.Should().BeTrue();
        request.Root.Should().Be(new ThunderstoreVersionRef(new ThunderstorePackageRef("Zehs", "REPOLib"), new ThunderstoreVersionNumber(4, 2, 0)));
        request.Game.ThunderstoreCommunity.Should().Be("repo");
    }

    [Fact]
    public async Task InstallHandsTheSourceThePackagesTheLoadoutAlreadyHas()
    {
        var loadout = await CreateLoadout();
        await Connection.LinkIntoLoadout(loadout, await Connection.AddThunderstoreLibraryPackage("4.0.0"));

        var (code, output) = await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "BepInEx-BepInExPack");

        code.Should().Be(0, output);
        _source.Requests.Single().InstalledInLoadout.Should().Equal(new Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber>
        {
            [new ThunderstorePackageRef("Zehs", "REPOLib")] = new(4, 0, 0),
        });
    }

    [Fact]
    public async Task InstallLeavesOutPackagesInDisabledGroups()
    {
        var loadout = await CreateLoadout();
        await Connection.LinkIntoLoadout(loadout, await Connection.AddThunderstoreLibraryPackage("4.0.0"), enabled: false);

        var (code, output) = await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "BepInEx-BepInExPack");

        code.Should().Be(0, output);
        _source.Requests.Single().InstalledInLoadout.Should().BeEmpty();
    }

    [Fact]
    public async Task InstallReportsAPackageTheSourceCannotResolveAndExitsWithOne()
    {
        var loadout = await CreateLoadout();
        _source.Failure = new DependencyResolutionException("Package version 'Zehs-REPOLib-4.2.0' does not exist");

        var (code, output) = await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib");

        code.Should().Be(1);
        output.Should().Contain("Package version 'Zehs-REPOLib-4.2.0' does not exist");
    }
}
