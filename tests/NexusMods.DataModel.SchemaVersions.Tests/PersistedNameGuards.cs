using System.Reflection;
using FluentAssertions;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.Sdk.Settings;

namespace NexusMods.DataModel.SchemaVersions.Tests;

public class PersistedNameGuards
{
    private static Assembly[] LoadAppAssemblies()
    {
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "NexusMods.*.dll"))
        {
            try { Assembly.LoadFrom(path); }
            catch (Exception) { /* native or mismatched dll, not ours to scan */ }
        }

        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("NexusMods.", StringComparison.Ordinal) == true)
            .ToArray();
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null)!; }
    }

    [Fact]
    public void JsonTypeNamesUseTheForkName()
    {
        var names = LoadAppAssemblies()
            .SelectMany(LoadableTypes)
            .SelectMany(t => t.GetCustomAttributes<JsonNameAttribute>(inherit: false).Select(a => (Type: t.FullName, Name: a.Name)))
            .ToArray();

        names.Should().NotBeEmpty("the scan found no [JsonName] types, so it is not looking in the right assemblies");
        names.Where(x => x.Name.StartsWith("NexusMods.", StringComparison.Ordinal))
            .Select(x => $"{x.Type}: {x.Name}")
            .Should().BeEmpty();
    }

    [Fact]
    public void SettingsIdsAreForkNamedAndUnique()
    {
        var ids = LoadAppAssemblies()
            .SelectMany(LoadableTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISettings).IsAssignableFrom(t))
            .Select(t => (Type: t.FullName!, Id: (string?)t.GetProperty(nameof(ISettings.SettingsId), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)))
            .ToArray();

        ids.Should().NotBeEmpty();
        ids.Where(x => string.IsNullOrWhiteSpace(x.Id)).Should().BeEmpty("every settings type needs a SettingsId");
        ids.Where(x => x.Id!.StartsWith("NexusMods.", StringComparison.Ordinal)).Should().BeEmpty("settings ids start with SilverModManager.");
        ids.GroupBy(x => x.Id).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty("two settings types must not share a file or row");
    }
}
