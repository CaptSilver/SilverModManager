using FluentAssertions;
using NexusMods.Networking.Thunderstore.DTOs;
using NexusMods.Sdk.Thunderstore;
using Xunit;

namespace NexusMods.Networking.Thunderstore.Tests;

public class ThunderstoreDependencyResolverTests
{
    private static readonly ThunderstorePackageRef BepInExPack = new("BepInEx", "BepInExPack");
    private static readonly ThunderstorePackageRef BepInExPackPeak = new("BepInEx", "BepInExPack_PEAK");
    private static readonly ThunderstorePackageRef MenuLib = new("nickklmao", "MenuLib");
    private static readonly ThunderstorePackageRef RepoConfig = new("nickklmao", "REPOConfig");
    private static readonly IReadOnlyList<ThunderstorePackageRef> Aliases =
    [
        new("BepInEx", "BepInExPack"),
        new("bbepis", "BepInExPack"),
        new("xiaoxiao921", "BepInExPack"),
    ];

    private readonly FakeLookup _lookup = new();

    private static ThunderstoreVersionNumber V(string text)
    {
        ThunderstoreVersionNumber.TryParse(text, out var version).Should().BeTrue();
        return version;
    }

    private static ThunderstoreVersionRef Ref(ThunderstorePackageRef package, string version) => new(package, V(version));

    private ThunderstoreDependencyResolver Resolver => new(_lookup);

    private static ThunderstoreDependencyResolver.ResolveRequest Request(
        ThunderstoreVersionRef root,
        DependencyPolicy policy = DependencyPolicy.Latest,
        IReadOnlyList<ThunderstorePackageRef>? aliases = null,
        ThunderstorePackageRef? loader = null,
        Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber>? installed = null)
        => new(root, policy, loader ?? new ThunderstorePackageRef("Unused", "Loader"), aliases ?? [], installed ?? new Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber>());

    private void RepoGraph()
    {
        _lookup.Add(BepInExPack, "5.4.2100");
        _lookup.Add(BepInExPack, "5.4.2305");
        _lookup.Add(MenuLib, "2.5.2", "BepInEx-BepInExPack-5.4.2305");
        _lookup.Add(RepoConfig, "1.2.6", "BepInEx-BepInExPack-5.4.2100", "nickklmao-MenuLib-2.5.2");
    }

    [Fact]
    public async Task Latest_TakesLatestOfEveryPackage_DependenciesFirst()
    {
        RepoGraph();
        _lookup.Add(MenuLib, "2.6.0", "BepInEx-BepInExPack-5.4.2305");

        var result = await Resolver.Resolve(Request(Ref(RepoConfig, "1.2.6")), CancellationToken.None);

        result.Should().Equal(Ref(BepInExPack, "5.4.2305"), Ref(MenuLib, "2.6.0"), Ref(RepoConfig, "1.2.6"));
    }

    [Fact]
    public async Task Exact_RootListedVersionsWinOverTransitiveRequests()
    {
        RepoGraph();
        _lookup.Add(MenuLib, "2.5.2", "BepInEx-BepInExPack-5.4.2305", "Other-Helper-1.0.0");
        _lookup.Add(new ThunderstorePackageRef("Other", "Helper"), "1.0.0");
        _lookup.Add(new ThunderstorePackageRef("Other", "Helper"), "1.1.0");

        var result = await Resolver.Resolve(Request(Ref(RepoConfig, "1.2.6"), DependencyPolicy.Exact), CancellationToken.None);

        result.Should().Equal(
            Ref(BepInExPack, "5.4.2100"),
            Ref(new ThunderstorePackageRef("Other", "Helper"), "1.1.0"),
            Ref(MenuLib, "2.5.2"),
            Ref(RepoConfig, "1.2.6"));
    }

    [Fact]
    public async Task Exact_RootPinOnTheGameLoaderItself_IsKept()
    {
        // R.E.P.O.'s loader is BepInEx-BepInExPack, which is also on the shared alias list.
        RepoGraph();

        var result = await Resolver.Resolve(Request(Ref(RepoConfig, "1.2.6"), DependencyPolicy.Exact, aliases: Aliases, loader: BepInExPack),
            CancellationToken.None);

        result.Should().Equal(Ref(BepInExPack, "5.4.2100"), Ref(MenuLib, "2.5.2"), Ref(RepoConfig, "1.2.6"));
    }

    [Fact]
    public async Task Exact_RootPinOnLoaderAlias_GameLoaderTakesLatest()
    {
        // An alias's version numbers say nothing about the game's own loader package.
        RepoGraph();
        _lookup.Add(BepInExPackPeak, "5.4.75301");

        var result = await Resolver.Resolve(Request(Ref(RepoConfig, "1.2.6"), DependencyPolicy.Exact, aliases: Aliases, loader: BepInExPackPeak),
            CancellationToken.None);

        result.Should().Equal(Ref(BepInExPackPeak, "5.4.75301"), Ref(MenuLib, "2.5.2"), Ref(RepoConfig, "1.2.6"));
    }

    [Fact]
    public async Task Installed_SkippedPackageStillPullsItsMissingDependencies()
    {
        var helper = new ThunderstorePackageRef("Other", "Helper");
        RepoGraph();
        _lookup.Add(MenuLib, "2.5.2", "BepInEx-BepInExPack-5.4.2305", "Other-Helper-1.0.0");
        _lookup.Add(helper, "1.0.0");

        var result = await Resolver.Resolve(Request(Ref(RepoConfig, "1.2.6"),
            installed: new() { [MenuLib] = V("2.5.2") }), CancellationToken.None);

        result.Should().Equal(Ref(BepInExPack, "5.4.2305"), Ref(helper, "1.0.0"), Ref(RepoConfig, "1.2.6"));
    }

    [Fact]
    public async Task EachPackageIsLookedUpOnce()
    {
        RepoGraph();

        await Resolver.Resolve(Request(Ref(RepoConfig, "1.2.6")), CancellationToken.None);

        _lookup.LookupCalls.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Cycle_VisitsEachOnce()
    {
        var a = new ThunderstorePackageRef("T", "A");
        var b = new ThunderstorePackageRef("T", "B");
        _lookup.Add(a, "1.0.0", "T-B-1.0.0");
        _lookup.Add(b, "1.0.0", "T-A-1.0.0");

        var result = await Resolver.Resolve(Request(Ref(a, "1.0.0")), CancellationToken.None);

        result.Should().Equal(Ref(b, "1.0.0"), Ref(a, "1.0.0"));
    }

    [Fact]
    public async Task Installed_EqualVersionDropped_OlderKept()
    {
        RepoGraph();

        var equal = await Resolver.Resolve(Request(Ref(RepoConfig, "1.2.6"),
            installed: new() { [MenuLib] = V("2.5.2") }), CancellationToken.None);
        var older = await Resolver.Resolve(Request(Ref(RepoConfig, "1.2.6"),
            installed: new() { [MenuLib] = V("2.0.0") }), CancellationToken.None);

        equal.Should().NotContain(r => r.Package == MenuLib);
        older.Should().Contain(Ref(MenuLib, "2.5.2"));
    }

    [Fact]
    public async Task Installed_AnyLoaderAlias_AddsNoLoader()
    {
        RepoGraph();
        _lookup.Add(BepInExPackPeak, "5.4.75301");

        var result = await Resolver.Resolve(Request(Ref(RepoConfig, "1.2.6"), aliases: Aliases, loader: BepInExPackPeak,
            installed: new() { [new ThunderstorePackageRef("bbepis", "BepInExPack")] = V("5.4.0") }), CancellationToken.None);

        result.Should().Equal(Ref(MenuLib, "2.5.2"), Ref(RepoConfig, "1.2.6"));
    }

    [Fact]
    public async Task LoaderAlias_IsReplacedByGameLoader()
    {
        var mod = new ThunderstorePackageRef("Someone", "PeakMod");
        _lookup.Add(BepInExPack, "5.4.2305");
        _lookup.Add(BepInExPackPeak, "5.4.75301");
        _lookup.Add(mod, "1.0.0", "BepInEx-BepInExPack-5.4.2305");

        var result = await Resolver.Resolve(Request(Ref(mod, "1.0.0"), aliases: Aliases, loader: BepInExPackPeak), CancellationToken.None);

        result.Should().Equal(Ref(BepInExPackPeak, "5.4.75301"), Ref(mod, "1.0.0"));
    }

    [Fact]
    public async Task HyphenatedNamespace_SplitConfirmedByLookup()
    {
        var legacy = new ThunderstorePackageRef("Legacy-Team", "Mod");
        var root = new ThunderstorePackageRef("T", "Root");
        _lookup.Add(legacy, "1.0.0");
        _lookup.Add(root, "1.0.0", "Legacy-Team-Mod-1.0.0");

        var result = await Resolver.Resolve(Request(Ref(root, "1.0.0")), CancellationToken.None);

        result.Should().Equal(Ref(legacy, "1.0.0"), Ref(root, "1.0.0"));
    }

    [Fact]
    public async Task UnknownDependency_ThrowsNamingStringAndDependent()
    {
        var root = new ThunderstorePackageRef("T", "Root");
        _lookup.Add(root, "1.0.0", "Legacy-Team-Mod-1.0.0");

        var act = async () => await Resolver.Resolve(Request(Ref(root, "1.0.0")), CancellationToken.None);

        (await act.Should().ThrowAsync<DependencyResolutionException>())
            .Which.Message.Should().Contain("Legacy-Team-Mod-1.0.0").And.Contain("T-Root-1.0.0");
    }

    [Fact]
    public async Task UnknownRoot_Throws()
    {
        var act = async () => await Resolver.Resolve(Request(Ref(RepoConfig, "1.2.6")), CancellationToken.None);

        await act.Should().ThrowAsync<DependencyResolutionException>();
    }

    private sealed class FakeLookup : IPackageLookup
    {
        private readonly Dictionary<ThunderstorePackageRef, List<PackageVersion>> _packages = new();

        public List<ThunderstorePackageRef> LookupCalls { get; } = [];

        public void Add(ThunderstorePackageRef package, string version, params string[] dependencies)
        {
            if (!_packages.TryGetValue(package, out var list)) _packages[package] = list = [];
            list.RemoveAll(v => v.VersionNumber == version);
            list.Add(new PackageVersion
            {
                Name = package.Name,
                FullName = $"{package}-{version}",
                VersionNumber = version,
                DownloadUrl = $"https://example/{package}-{version}.zip",
                Dependencies = dependencies,
            });
        }

        private static ThunderstoreVersionNumber Parse(PackageVersion v)
        {
            ThunderstoreVersionNumber.TryParse(v.VersionNumber, out var n);
            return n;
        }

        public ValueTask<PackageDetail?> Lookup(ThunderstorePackageRef package, CancellationToken cancellationToken)
        {
            LookupCalls.Add(package);
            if (!_packages.TryGetValue(package, out var list)) return ValueTask.FromResult<PackageDetail?>(null);
            return ValueTask.FromResult<PackageDetail?>(new PackageDetail
            {
                Namespace = package.Namespace,
                Name = package.Name,
                Latest = list.OrderByDescending(Parse).First(),
            });
        }

        public ValueTask<PackageVersion?> LookupVersion(ThunderstoreVersionRef version, CancellationToken cancellationToken)
        {
            var found = _packages.TryGetValue(version.Package, out var list)
                ? list.FirstOrDefault(v => Parse(v) == version.Version)
                : null;
            return ValueTask.FromResult(found);
        }
    }
}
