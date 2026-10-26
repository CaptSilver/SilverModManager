using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace SilverModManager.Games.Generic.ModProvenance;

/// <summary>
/// Works out where the mods a game folder holds came from, from evidence other than the folder's own name: other mod
/// managers' records, a lock file the caller read, and the archives still on disk. Earlier sources win; a later source
/// that names something else is reported, not silently dropped. Nothing here throws on what a file or the network says:
/// a source that fails is reported and the others still count.
/// </summary>
public sealed class ModIdentifier(ILogger? logger = null)
{
    public async Task<IdentifyResult> IdentifyAsync(IdentifyRequest request, IArchiveCatalog? catalog, CancellationToken cancellationToken)
    {
        var problems = ImmutableArray.CreateBuilder<string>();
        var managers = await Task.Run(() => ManagerRecords.Read(request.Search, request.Game), cancellationToken);
        problems.AddRange(managers.Problems);

        var claims = request.Mods.ToDictionary(
            mod => mod,
            mod => managers.Claims.Concat(request.LockClaims).Where(c => Matches(c, mod)).OrderBy(c => c.Method).ToList());

        if (catalog is not null && request.Game.NexusGameId is { } game)
        {
            var needy = claims.Where(p => p.Value.FirstOrDefault() is not { } first || first is { Source: ClaimSource.Nexus, FileId: null }).Select(p => p.Key).ToArray();
            if (needy.Length > 0)
            {
                var folders = managers.ArchiveFolders.Concat(request.Search.ExtraArchiveFolders);
                if (request.Search.Home.Length > 0) folders = folders.Append(Path.Combine(request.Search.Home, "Downloads"));
                var search = await ArchiveIdentifier.FindAsync(catalog, game, folders, cancellationToken, needy);
                problems.AddRange(search.Problems);

                foreach (var mod in needy)
                {
                    // Several uploads of one mod can sit in Downloads; the newest file id is the latest version.
                    var hit = search.Matches
                        .Where(m => Names.Squash(ArchiveIdentifier.NamePart(m.Hit.FileName)) == Names.Squash(mod))
                        .OrderByDescending(m => m.Hit.FileId)
                        .FirstOrDefault();
                    if (hit is not null)
                        claims[mod].Add(new ModClaim(mod, ClaimMethod.Archive, Path.GetFileName(hit.Path), ClaimSource.Nexus, hit.Hit.ModId, hit.Hit.FileId, null, hit.Hit.FileName));
                }
            }
        }

        var mods = request.Mods.Select(mod => Decide(mod, claims[mod])).ToImmutableArray();
        return new IdentifyResult(mods, problems.ToImmutable());
    }

    private ModIdentification Decide(string mod, List<ModClaim> claims)
    {
        var ordered = claims.OrderBy(c => c.Method).ToList();
        if (ordered.Count == 0) return new ModIdentification(mod, null, []);

        var chosen = ordered[0];

        // A record that knows the page but not the file takes the exact file from an archive of that page, rather than
        // leaving the planner to guess by version.
        if (chosen is { Source: ClaimSource.Nexus, FileId: null, ModId: { } page }
            && ordered.Skip(1).FirstOrDefault(o => o.ModId == page && o.FileId is not null) is { } exact)
            chosen = chosen with { FileId = exact.FileId };

        var disagreements = ImmutableArray.CreateBuilder<string>();
        foreach (var other in ordered.Skip(1).Where(o => Differs(chosen, o)))
        {
            var sentence = $"{mod}: {Source(chosen)} say {chosen.Describe()}, but {Source(other)} say {other.Describe()}. Using {Source(chosen)}.";
            disagreements.Add(sentence);
            logger?.LogWarning("{Disagreement}", sentence);
        }

        return new ModIdentification(mod, chosen, disagreements.ToImmutable());
    }

    private static string Source(ModClaim claim) => claim.Method switch
    {
        ClaimMethod.ManagerRecords => $"{claim.Origin}'s records",
        ClaimMethod.LockFile => $"{claim.Origin}'s lock file",
        _ => $"the archive {claim.Origin}",
    };

    private static bool Differs(ModClaim a, ModClaim b)
    {
        if (a.Source != b.Source) return false;
        if (a.Source == ClaimSource.Thunderstore) return !string.Equals(a.Package, b.Package, StringComparison.OrdinalIgnoreCase);
        if (a.FileId is { } fa && b.FileId is { } fb) return fa != fb;
        return a.ModId is { } ma && b.ModId is { } mb && ma != mb;
    }

    /// <summary>Whether <paramref name="claim"/> is about the mod called <paramref name="mod"/>: by the claim's own name, its package or its archive's name.</summary>
    public static bool Matches(ModClaim claim, string mod)
    {
        var wanted = Names.Squash(mod);
        if (wanted.Length == 0) return false;

        return new[] { claim.Mod, claim.Package, Names.ShortPackage(claim.Package), claim.Archive is null ? null : ArchiveIdentifier.NamePart(claim.Archive) }
            .Any(candidate => Names.Squash(candidate) == wanted);
    }
}
