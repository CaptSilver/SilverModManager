using JetBrains.Annotations;
using NexusMods.Networking.Thunderstore.DTOs;
using NexusMods.Sdk.Thunderstore;

namespace NexusMods.Networking.Thunderstore;

/// <summary>
/// Works out which package versions have to be fetched to install one package. All knowledge of the packages
/// comes through <see cref="IPackageLookup"/>; where downloads go and what the library already holds are
/// the caller's business.
/// </summary>
[PublicAPI]
public sealed class ThunderstoreDependencyResolver(IPackageLookup lookup)
{
    /// <param name="Root">The version being installed.</param>
    /// <param name="Policy">Which versions of dependencies to take.</param>
    /// <param name="Loader">The game's mod loader package. Any dependency on it or an alias becomes a dependency on this.</param>
    /// <param name="LoaderAliases">Other packages that ship the same loader under another name.</param>
    /// <param name="InstalledInLoadout">Packages already in the loadout, with their versions.</param>
    public sealed record ResolveRequest(
        ThunderstoreVersionRef Root,
        DependencyPolicy Policy,
        ThunderstorePackageRef Loader,
        IReadOnlyList<ThunderstorePackageRef> LoaderAliases,
        IReadOnlyDictionary<ThunderstorePackageRef, ThunderstoreVersionNumber> InstalledInLoadout);

    /// <summary>
    /// The versions to fetch, dependencies before dependents, the root last, one entry per package.
    /// </summary>
    /// <exception cref="DependencyResolutionException">The root or one of its dependencies does not exist.</exception>
    public async Task<IReadOnlyList<ThunderstoreVersionRef>> Resolve(ResolveRequest request, CancellationToken cancellationToken)
    {
        var rootVersion = await lookup.LookupVersion(request.Root, cancellationToken)
            ?? throw new DependencyResolutionException($"Package version '{request.Root}' does not exist");

        var state = new State(request, cancellationToken);
        state.Visited.Add(request.Root.Package);

        if (request.Policy == DependencyPolicy.Exact)
        {
            foreach (var text in rootVersion.Dependencies)
            {
                var (package, version) = await ParseDependency(state, text, request.Root);
                // A pin on an alias is dropped: its version numbers say nothing about the game's own loader.
                if (package == request.Loader || !state.IsLoader(package)) state.RootListed.TryAdd(package, version);
            }
        }

        await VisitDependencies(state, rootVersion, request.Root);
        state.Result.Add(request.Root);
        return state.Result;
    }

    private async Task VisitDependencies(State state, PackageVersion version, ThunderstoreVersionRef dependent)
    {
        foreach (var text in version.Dependencies)
        {
            var (package, _) = await ParseDependency(state, text, dependent);
            await Visit(state, state.Canonical(package));
        }
    }

    private async Task Visit(State state, ThunderstorePackageRef package)
    {
        if (!state.Visited.Add(package)) return;

        var chosen = await Choose(state, package);
        if (chosen is null) return;

        // An installed package is not fetched again, but its dependencies are still walked: the user may have
        // removed one, and the ones still installed drop out here on their own.
        await VisitDependencies(state, chosen.Value.Details, chosen.Value.Ref);
        if (state.Request.InstalledInLoadout.TryGetValue(package, out var installed) && installed >= chosen.Value.Version) return;
        state.Result.Add(chosen.Value.Ref);
    }

    // Null means the package is deliberately not installed (the game already has a loader).
    private async Task<(ThunderstoreVersionRef Ref, ThunderstoreVersionNumber Version, PackageVersion Details)?> Choose(State state, ThunderstorePackageRef package)
    {
        if (package == state.Request.Loader && state.LoaderInstalled) return null;

        if (state.Request.Policy == DependencyPolicy.Exact && state.RootListed.TryGetValue(package, out var exact))
        {
            var pinned = new ThunderstoreVersionRef(package, exact);
            var details = await lookup.LookupVersion(pinned, state.Token)
                ?? throw new DependencyResolutionException($"Package version '{pinned}' does not exist");
            return (pinned, exact, details);
        }

        var detail = await LookupPackage(state, package)
            ?? throw new DependencyResolutionException($"Package '{package}' does not exist");
        if (!ThunderstoreVersionNumber.TryParse(detail.Latest.VersionNumber, out var latest))
            throw new DependencyResolutionException($"Package '{package}' has an unreadable latest version '{detail.Latest.VersionNumber}'");
        return (new ThunderstoreVersionRef(package, latest), latest, detail.Latest);
    }

    private async Task<(ThunderstorePackageRef Package, ThunderstoreVersionNumber Version)> ParseDependency(State state, string text, ThunderstoreVersionRef dependent)
    {
        if (!ThunderstoreDependencyString.TryParse(text, out var dependency))
            throw new DependencyResolutionException($"Dependency '{text}' of '{dependent}' is not a valid dependency string");

        foreach (var candidate in dependency.Candidates())
        {
            if (!ThunderstorePackageRef.TryCreate(candidate.Namespace, candidate.Name, out var package)) continue;
            if (state.IsLoader(package)) return (package, dependency.VersionNumber);
            if (await LookupPackage(state, package) is not null) return (package, dependency.VersionNumber);
        }

        throw new DependencyResolutionException($"Dependency '{text}' of '{dependent}' does not match any Thunderstore package");
    }

    // Confirming a split and choosing a version both need the package, and over the web API each lookup is a
    // request, so misses are remembered too.
    private async ValueTask<PackageDetail?> LookupPackage(State state, ThunderstorePackageRef package)
    {
        if (state.Packages.TryGetValue(package, out var known)) return known;
        var detail = await lookup.Lookup(package, state.Token);
        state.Packages[package] = detail;
        return detail;
    }

    private sealed class State(ResolveRequest request, CancellationToken token)
    {
        public ResolveRequest Request { get; } = request;
        public CancellationToken Token { get; } = token;
        public HashSet<ThunderstorePackageRef> Visited { get; } = [];
        public Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber> RootListed { get; } = [];
        public List<ThunderstoreVersionRef> Result { get; } = [];
        public Dictionary<ThunderstorePackageRef, PackageDetail?> Packages { get; } = [];

        // An older install of any alias still provides a working loader, so it never gets replaced.
        public bool LoaderInstalled { get; } = request.InstalledInLoadout.ContainsKey(request.Loader)
            || request.LoaderAliases.Any(request.InstalledInLoadout.ContainsKey);

        public bool IsLoader(ThunderstorePackageRef package) => package == Request.Loader || Request.LoaderAliases.Contains(package);

        public ThunderstorePackageRef Canonical(ThunderstorePackageRef package) => IsLoader(package) ? Request.Loader : package;
    }
}
