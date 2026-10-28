using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;
using SilverModManager.UI.Sdk.Settings;

namespace SilverModManager.App.Commandline;

/// <summary>
/// Reads and changes the settings the Settings page shows, for scripts and MCP clients.
/// </summary>
public static class SettingsVerbs
{
    public static IServiceCollection AddSettingsVerbs(this IServiceCollection services) =>
        services
            .AddModule("settings", "Commands for the app's settings")
            .AddVerb(() => List)
            .AddVerb(() => Get)
            .AddVerb(() => Set);

    private sealed record SettingRow(string Key, string Name, string Section, string Value, string Default, bool RequiresRestart);

    private static SettingRow ToRow(SettingEntry entry, ISettingsManager settings, IEnumerable<SectionDescriptor> sections) => new(
        entry.Key,
        entry.Property.Options.DisplayName,
        sections.FirstOrDefault(s => s.Id == entry.Property.Options.Section)?.Name ?? entry.Property.Options.Section.ToString(),
        SettingsCatalog.Format(entry.Property.GetValue(settings)),
        SettingsCatalog.Format(entry.Property.GetDefaultValue(settings)),
        entry.Property.Options.RequiresRestart);

    [Verb("settings list", "Lists every setting with its key, current value and default")]
    private static async Task<int> List([Injected] IRenderer renderer,
        [Injected] ISettingsManager settings,
        [Injected] IEnumerable<SectionDescriptor> sections,
        [Option("j", "json", "Print the settings as JSON", true)] bool json)
    {
        var rows = SettingsCatalog.List(settings).Select(entry => ToRow(entry, settings, sections)).ToArray();

        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        await renderer.Table(
            ["Key", "Name", "Section", "Value", "Default", "Restart"],
            rows.Select(r => new object[] { r.Key, r.Name, r.Section, r.Value, r.Default, r.RequiresRestart ? "yes" : "no" }));
        return 0;
    }

    [Verb("settings get", "Prints the current value of one setting")]
    private static async Task<int> Get([Injected] IRenderer renderer,
        [Injected] ISettingsManager settings,
        [Option("k", "key", "The setting's key, as `settings list` shows it, e.g. HttpProxySettings.HttpProxy")] string key)
    {
        var entry = SettingsCatalog.Find(settings, key);
        await renderer.TextLine($"{entry.Key} = {SettingsCatalog.Format(entry.Property.GetValue(settings))}");
        return 0;
    }

    [Verb("settings set", "Changes one setting; an empty value clears a text setting")]
    private static async Task<int> Set([Injected] IRenderer renderer,
        [Injected] ISettingsManager settings,
        [Option("k", "key", "The setting's key, as `settings list` shows it, e.g. HttpProxySettings.HttpProxy")] string key,
        [Option("v", "value", "The new value: true or false, a number, an enum member by name, or text")] string value)
    {
        var entry = SettingsCatalog.Find(settings, key);
        var parsed = SettingsCatalog.Parse(entry, value);
        entry.Property.Update(settings, parsed);

        var message = $"{entry.Key} is now '{SettingsCatalog.Format(parsed)}'.";
        if (entry.Property.Options.RequiresRestart) message += " Restart SilverModManager for it to take effect.";
        await renderer.TextLine(message);
        return 0;
    }
}
