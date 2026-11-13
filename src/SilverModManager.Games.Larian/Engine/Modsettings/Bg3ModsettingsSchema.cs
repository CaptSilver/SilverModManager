using SilverModManager.Games.Larian.Engine.GameInstalled;

namespace SilverModManager.Games.Larian.Engine.Modsettings;

/// <summary>
/// Baldur's Gate 3's <c>modsettings.lsx</c>. An entry is the game's unless it carries a mod.io handle, which the
/// game gives only to modules it downloaded.
/// </summary>
public sealed class Bg3ModsettingsSchema : IModsettingsSchema
{
    public ModsettingsDocument Parse(string xml) => ModsettingsDocument.Parse(xml);

    public string Render(IReadOnlyDictionary<string, string> version, IEnumerable<ModsettingsDocument.ModuleEntry> entries, ModsettingsDocument? layoutFrom) =>
        ModsettingsDocument.Render(version, entries, layoutFrom);

    public IReadOnlyDictionary<string, string> DefaultVersion { get; } = new Dictionary<string, string>
    {
        ["major"] = "4", ["minor"] = "8", ["revision"] = "0", ["build"] = "400",
    };

    public IReadOnlyList<ModsettingsDocument.ModuleEntry> GameEntries(ModsettingsDocument document, IReadOnlySet<string> adventureUuids) =>
        document.Entries.Where(e => !e.IsPublished).ToList();

    public bool CanCreateFromNothing => true;

    public IReadOnlyList<ModsettingsDocument.ModuleEntry> EntriesWhenNoFile { get; } =
    [
        new("GustavX", "GustavX", "0", "cb555efe-2d9e-131f-8195-a89329d218ea", "145241302737902957", ""),
        new("HonourX", "HonourX", "0", "767d0062-d82c-279c-e16b-dfee7fe94cdd", "36028797025827000", ""),
    ];
}
