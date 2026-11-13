using SilverModManager.Games.Larian.BaldursGate3.GameInstalled;

namespace SilverModManager.Games.Larian.BaldursGate3.ModList;

/// <summary>
/// A pak of the loadout and what the game's mod list would say about it.
/// </summary>
/// <param name="IsActive">Whether the pak should be in the mod list: the order's verdict, owning mod's state included.</param>
/// <param name="Entry">The module entry the pak gets, or null when the pak could not be read.</param>
public sealed record Bg3PakCandidate(string Name, bool IsGameInstalled, bool IsActive, ModsettingsDocument.ModuleEntry? Entry);

/// <summary>Two active paks that provide the same module; only <paramref name="KeptPak"/> is written.</summary>
public sealed record Bg3DuplicateModule(string Uuid, string ModuleName, string KeptPak, IReadOnlyList<string> IgnoredPaks);

/// <summary>
/// The paks the app writes to <c>modsettings.lsx</c>, in order, worked out once per sync so the writer and the
/// comparison with the game's file read the same list.
/// </summary>
public sealed class Bg3ModListPlan
{
    private Bg3ModListPlan(IReadOnlyList<Bg3PakCandidate> paks, IReadOnlyList<Bg3DuplicateModule> duplicates)
    {
        Paks = paks;
        Duplicates = duplicates;
    }

    /// <summary>The paks to write, in order. Every one has an entry.</summary>
    public IReadOnlyList<Bg3PakCandidate> Paks { get; }

    public IReadOnlyList<Bg3DuplicateModule> Duplicates { get; }

    public IReadOnlyList<string> Names => Paks.Select(p => p.Name).ToList();

    /// <summary>
    /// Takes the active paks of <paramref name="inOrder"/>. A pak that cannot be read has no entry to write and is
    /// left out. When two active paks are the same module, the game would load whichever it saw first and report
    /// it twice, so one is written: the app's own copy over one the game installed, otherwise the earlier pak.
    /// </summary>
    public static Bg3ModListPlan Build(IReadOnlyList<Bg3PakCandidate> inOrder)
    {
        var active = inOrder
            .Where(p => p.IsActive)
            .SelectMany(p => p.Entry is { } entry ? [(Pak: p, Entry: entry)] : Array.Empty<(Bg3PakCandidate Pak, ModsettingsDocument.ModuleEntry Entry)>())
            .ToList();

        var kept = new Dictionary<string, (Bg3PakCandidate Pak, ModsettingsDocument.ModuleEntry Entry)>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in active)
        {
            var uuid = candidate.Entry.Uuid;
            if (!kept.TryGetValue(uuid, out var current) || (current.Pak.IsGameInstalled && !candidate.Pak.IsGameInstalled))
                kept[uuid] = candidate;
        }

        var paks = active.Where(a => ReferenceEquals(kept[a.Entry.Uuid].Pak, a.Pak)).Select(a => a.Pak).ToList();
        var duplicates = active
            .GroupBy(a => a.Entry.Uuid, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => new Bg3DuplicateModule(
                g.Key,
                kept[g.Key].Entry.Name,
                kept[g.Key].Pak.Name,
                g.Where(a => !ReferenceEquals(a.Pak, kept[g.Key].Pak)).Select(a => a.Pak.Name).ToList()))
            .ToList();

        return new Bg3ModListPlan(paks, duplicates);
    }
}
