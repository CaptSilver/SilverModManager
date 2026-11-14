using JetBrains.Annotations;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.UI.Sdk.Settings;

[PublicAPI]
public static class SettingsTabs
{
    /// <summary>The header of a tab: what it is called and the 16 px glyph beside the name.</summary>
    public static (string Name, IconValue Icon) Describe(SettingsTab tab) => tab switch
    {
        SettingsTab.General => ("General", IconValues.Desktop),
        SettingsTab.Appearance => ("Appearance", IconValues.ColorLens),
        SettingsTab.Downloads => ("Downloads", IconValues.Download),
        SettingsTab.Accounts => ("Accounts & API keys", IconValues.Account),
        SettingsTab.Developer => ("Developer", IconValues.Code),
        _ => throw new ArgumentOutOfRangeException(nameof(tab), tab, null),
    };

    /// <summary>
    /// Tabs in enum order, each holding the sections that declare it, highest <see cref="SectionDescriptor.Priority"/> first
    /// (the old single page listed sections that way), and each section's rows by display name. A tab with no rows, and a
    /// section with no rows, is left out. Rows whose section has no descriptor are dropped, as the page always did.
    /// </summary>
    public static IReadOnlyList<TabGroup<T>> Group<T>(
        IEnumerable<SectionDescriptor> sections,
        IEnumerable<T> rows,
        Func<T, SectionId> sectionOf,
        Func<T, string> displayNameOf)
    {
        var descriptors = sections.ToDictionary(s => s.Id);
        var bySection = rows
            .Where(row => descriptors.ContainsKey(sectionOf(row)))
            .OrderBy(displayNameOf, StringComparer.OrdinalIgnoreCase)
            .GroupBy(sectionOf)
            .ToArray();

        var result = new List<TabGroup<T>>();
        foreach (var tab in Enum.GetValues<SettingsTab>())
        {
            var groups = bySection
                .Where(g => descriptors[g.Key].Tab == tab)
                .OrderByDescending(g => descriptors[g.Key].Priority)
                .Select(g => new SectionGroup<T>(descriptors[g.Key], g.ToArray()))
                .ToArray();

            if (groups.Length > 0) result.Add(new TabGroup<T>(tab, groups));
        }

        return result;
    }
}

[PublicAPI]
public sealed record TabGroup<T>(SettingsTab Tab, IReadOnlyList<SectionGroup<T>> Sections)
{
    /// <summary>A header is only worth drawing when the tab holds more than one section.</summary>
    public bool ShowsSectionHeaders => Sections.Count > 1;
}

[PublicAPI]
public sealed record SectionGroup<T>(SectionDescriptor Section, IReadOnlyList<T> Rows);
