using System.Reflection;
using FluentAssertions;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;

namespace NexusMods.DataModel.SchemaVersions.Tests;

public class PersistedNameGuards
{
    /// <summary>
    /// Fewest first-party assemblies the scan may find before it is considered to be looking in the wrong
    /// place. Set at about half of what the output directory holds; count them with
    /// <c>ls tests/NexusMods.DataModel.SchemaVersions.Tests/bin/Debug/net*/NexusMods.*.dll | wc -l</c> (the
    /// test run also writes the real count to <c>first-party-assemblies.txt</c> in that directory).
    /// </summary>
    internal const int MinimumFirstPartyAssemblies = 40;

    internal static Assembly[] LoadAppAssemblies() => LoadAppAssemblies(FirstPartyNames.AssemblyGlob, FirstPartyNames.AssemblyPrefix, MinimumFirstPartyAssemblies);

    /// <summary>
    /// Loads every first-party DLL next to the test, the unprefixed App assembly included, and returns the
    /// loaded first-party assemblies. Throws when fewer than <paramref name="floor"/> are found, so a scan
    /// pointed at nothing cannot make the guards that use it pass vacuously.
    /// </summary>
    internal static Assembly[] LoadAppAssemblies(string glob, string prefix, int floor)
    {
        var paths = Directory.EnumerateFiles(AppContext.BaseDirectory, glob)
            .Append(Path.Combine(AppContext.BaseDirectory, FirstPartyNames.AppAssemblyName + ".dll"))
            .Where(File.Exists);

        foreach (var path in paths)
        {
            try { Assembly.LoadFrom(path); }
            catch (Exception) { /* native or mismatched dll, not ours to scan */ }
        }

        var found = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name is { } name
                && (name.StartsWith(prefix, StringComparison.Ordinal) || name == FirstPartyNames.AppAssemblyName))
            .ToArray();

        if (found.Length < floor)
            throw new InvalidOperationException($"The first-party assembly scan found {found.Length} assemblies (prefix '{prefix}', glob '{glob}') in {AppContext.BaseDirectory}, expected at least {floor}");

        return found;
    }

    internal static IEnumerable<Type> LoadableTypes(Assembly assembly)
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
        names.Where(x => x.Name.StartsWith(FirstPartyNames.UpstreamPersistedPrefix, StringComparison.Ordinal))
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
        ids.Where(x => x.Id!.StartsWith(FirstPartyNames.UpstreamPersistedPrefix, StringComparison.Ordinal)).Should().BeEmpty("settings ids start with SilverModManager.");
        ids.GroupBy(x => x.Id).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty("two settings types must not share a file or row");
    }
}
