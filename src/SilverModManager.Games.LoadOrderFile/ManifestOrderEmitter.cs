using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Games.LoadOrderFile.Manifests;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// The order rules mods declare in their manifests (load after, load before, incompatible with) checked against the
/// order the app writes. Only pairs with at least one mod from the loadout are reported: the order of the mods the
/// game folder already held is the game's and the player's, and this app has no way to move them.
/// </summary>
public sealed class ManifestOrderEmitter(IFileStore fileStore, ILogger<GeneratedOrderFile> logger) : ILoadoutDiagnosticEmitter
{
    private sealed record Party(string Name, ModManifest? Manifest, ManagedModEntry? Managed, string? WorkshopItem);

    /// <param name="Involved">The loadout mod of the pair, whose group the report points at.</param>
    private sealed record Violation(Party Mod, Party Target, bool LoadAfter, bool Forced, ManagedModEntry Involved);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (loadout.InstallationInstance.Game is not LoadOrderFileGame game || game.Definition.ManifestReader is null) yield break;

        var definition = game.Definition;
        var locations = loadout.InstallationInstance.Locations;
        var gameFolder = locations.ToAbsolutePath(new GamePath(LocationId.Game, NexusMods.Paths.RelativePath.Empty));

        var managed = await ManagedModList.Read(definition, loadout, fileStore, cancellationToken);
        var order = await new GeneratedOrderFile(definition, logger).Order(loadout, managed, cancellationToken);
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++) position.TryAdd(order[i], i);

        var parties = PartiesOf(definition, gameFolder, managed);
        var managedNames = managed.Select(mod => mod.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lastExternal = order.Select((name, index) => managedNames.Contains(name) ? -1 : index).DefaultIfEmpty(-1).Max();
        var inOrder = order.Select(name => parties.GetValueOrDefault(name)).OfType<Party>().Where(party => party.Manifest is not null).ToList();

        foreach (var violation in OrderViolations(inOrder, parties, position))
        {
            var diagnostic = Diagnostics.CreateOrderViolation(
                Group: violation.Involved.Group.ToReference(loadout),
                Mod: violation.Mod.Name,
                Target: violation.Target.Name,
                Rule: violation.LoadAfter ? "load after" : "load before",
                FixPage: FixPage(game, violation, position, lastExternal));
            yield return violation.Forced ? diagnostic with { Severity = DiagnosticSeverity.Critical } : diagnostic;
        }

        foreach (var (mod, other, involved) in IncompatiblePairs(inOrder, parties, position))
        {
            yield return Diagnostics.CreateIncompatible(
                Group: involved.Group.ToReference(loadout),
                Mod: mod.Name,
                Other: other.Name);
        }
    }

    /// <summary>Every mod of the loadout and of the external sources, by the name the order file lists it under.</summary>
    private static Dictionary<string, Party> PartiesOf(LoadOrderFileGameDefinition definition, NexusMods.Paths.AbsolutePath gameFolder, IReadOnlyList<ManagedModEntry> managed)
    {
        var parties = new Dictionary<string, Party>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in managed) parties.TryAdd(mod.Name, new Party(mod.Name, mod.Manifest, mod, null));

        foreach (var source in definition.ExternalMods)
        {
            foreach (var external in source.FindMods(gameFolder))
                parties.TryAdd(external.Manifest.Id, new Party(external.Manifest.Id, external.Manifest, null, source is SteamWorkshopModSource ? external.FolderName : null));
        }

        return parties;
    }

    /// <summary>Each rule a mod's manifest gives that the order breaks, once per pair of mods, in the order's order.</summary>
    private static IEnumerable<Violation> OrderViolations(IReadOnlyList<Party> inOrder, Dictionary<string, Party> parties, Dictionary<string, int> position)
    {
        // The key is "first must come before second", so a pair both mods state is reported once; a forced rule wins.
        var found = new Dictionary<(string First, string Second), Violation>();
        foreach (var mod in inOrder)
        {
            if (mod.Manifest is not { } manifest) continue;

            var rules = manifest.LoadAfter.Select(target => (target, after: true, forced: false))
                .Concat(manifest.ForceLoadAfter.Select(target => (target, after: true, forced: true)))
                .Concat(manifest.LoadBefore.Select(target => (target, after: false, forced: false)))
                .Concat(manifest.ForceLoadBefore.Select(target => (target, after: false, forced: true)));

            foreach (var (target, after, forced) in rules)
            {
                if (!position.TryGetValue(target, out var targetPosition) || !parties.TryGetValue(target, out var targetParty)) continue;
                if (targetParty == mod || (mod.Managed ?? targetParty.Managed) is not { } involved) continue;
                if (after ? targetPosition < position[mod.Name] : targetPosition > position[mod.Name]) continue;

                var key = after ? (targetParty.Name, mod.Name) : (mod.Name, targetParty.Name);
                var violation = new Violation(mod, targetParty, after, forced, involved);
                if (!found.TryGetValue(key, out var known) || (forced && !known.Forced)) found[key] = violation;
            }
        }

        return found.Values;
    }

    private static IEnumerable<(Party Mod, Party Other, ManagedModEntry Involved)> IncompatiblePairs(IReadOnlyList<Party> inOrder, Dictionary<string, Party> parties, Dictionary<string, int> position)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var mod in inOrder)
        {
            if (mod.Manifest is not { } manifest) continue;

            foreach (var target in manifest.IncompatibleWith)
            {
                if (!position.ContainsKey(target) || !parties.TryGetValue(target, out var other) || other == mod) continue;
                if ((mod.Managed ?? other.Managed) is not { } involved) continue;

                var key = string.CompareOrdinal(mod.Name, other.Name) <= 0 ? (mod.Name, other.Name) : (other.Name, mod.Name);
                if (seen.Add(key)) yield return (mod, other, involved);
            }
        }
    }

    /// <summary>
    /// The Load Order page when both mods are the loadout's. Otherwise the external mod can only be moved in the game's
    /// own list, and that helps only when the loadout's mod sits among the external ones (placed before an external mod
    /// its manifest names): every other loadout mod is written after all of them, whatever the game's list says.
    /// </summary>
    private static string FixPage(LoadOrderFileGame game, Violation violation, Dictionary<string, int> position, int lastExternal)
    {
        if (violation.Mod.Managed is not null && violation.Target.Managed is not null)
            return DefaultPriorityOrderSink.PriorityPage(game);

        var (managed, external) = violation.Mod.Managed is not null ? (violation.Mod, violation.Target) : (violation.Target, violation.Mod);
        var item = external.WorkshopItem is { } id ? $" (Steam Workshop item {id})" : "";
        var gameList = $"{game.Definition.DisplayName}'s mod list";

        return position[managed.Name] < lastExternal
            ? $"move {external.Name} in {gameList}, whose order the app keeps for the mods it does not manage{item}"
            : $"the app writes the mods it does not manage ahead of its own, whatever order {gameList} gives them; install {external.Name} through the app to order the two on the {DefaultPriorityOrderSink.PriorityPage(game)} page{item}";
    }
}
