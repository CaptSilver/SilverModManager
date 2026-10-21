using System.Text.RegularExpressions;
using NexusMods.Games.Declarative.Capabilities;

namespace NexusMods.Games.Declarative.Schema;

/// <summary>
/// The structural checks YAML deserialisation can't do. It checks shape only: it never looks a Nexus
/// domain up, since the catalog loads before the container, and the mapping cache that does the lookup
/// lives in it and blocks on the network when it misses.
/// </summary>
public sealed partial class DefinitionValidator(ICapabilityRegistry capabilities)
{
    public const string GameLocation = "Game";

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex DomainPattern();

    /// <summary>The problems with <paramref name="definition"/>; empty when it is valid.</summary>
    public IReadOnlyList<string> Validate(GameDefinition definition)
    {
        var problems = new List<string>();

        if (!IdPattern().IsMatch(definition.Id)) problems.Add($"id '{definition.Id}' must match ^[a-z0-9-]+$");
        if (string.IsNullOrWhiteSpace(definition.Name)) problems.Add("name is required");

        ValidateNexus(definition, problems);
        ValidateStores(definition, problems);
        ValidatePrimaryExe(definition, problems);
        ValidateLocations(definition, problems);
        ValidateInstaller(definition, problems);
        ValidateCapabilities(definition, problems);

        return problems;
    }

    private static void ValidateNexus(GameDefinition definition, List<string> problems)
    {
        if (definition.Nexus is not { } nexus) return;
        if (nexus.Id <= 0) problems.Add($"nexus.id must be greater than 0, found {nexus.Id}");
        if (!DomainPattern().IsMatch(nexus.Domain)) problems.Add($"nexus.domain '{nexus.Domain}' must match ^[a-z0-9]+$");
    }

    private static void ValidateStores(GameDefinition definition, List<string> problems)
    {
        var stores = definition.Stores;
        if (stores.Steam.Contains(0u)) problems.Add("stores.steam holds 0, which is not an app id");
        if (stores.Gog.Any(id => id <= 0)) problems.Add("stores.gog holds an id that is not a positive product id");
        if (stores.Epic.Any(string.IsNullOrWhiteSpace)) problems.Add("stores.epic holds an empty catalog item id");
        if (stores.Amazon.Any(string.IsNullOrWhiteSpace)) problems.Add("stores.amazon holds an empty product id");

        var hasStoreId = stores.Steam.Length + stores.Gog.Length + stores.Epic.Length + stores.Amazon.Length > 0;
        if (!hasStoreId && !stores.ManualOnly) problems.Add("stores needs at least one store id, or manualOnly: true");
    }

    private static void ValidatePrimaryExe(GameDefinition definition, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(definition.PrimaryExe)) problems.Add("primaryExe is required");
        else if (!IsRelativeWithoutParent(definition.PrimaryExe)) problems.Add($"primaryExe '{definition.PrimaryExe}' must be a relative path without '..'");
    }

    private static void ValidateLocations(GameDefinition definition, List<string> problems)
    {
        if (!definition.Locations.TryGetValue(GameLocation, out var game)) problems.Add($"locations.{GameLocation} is required");
        else if (game.Root != LocationRoots.Install) problems.Add($"locations.{GameLocation} must have root: {LocationRoots.Install}, found '{game.Root}'");

        foreach (var (name, location) in definition.Locations)
        {
            if (string.IsNullOrWhiteSpace(name)) problems.Add("a location has an empty name");
            if (!LocationRoots.IsKnown(location.Root)) problems.Add($"locations.{name} has unknown root '{location.Root}', expected one of {string.Join(", ", LocationRoots.All)}");
            if (location.Path.Length > 0 && !IsRelativeWithoutParent(location.Path)) problems.Add($"locations.{name}.path '{location.Path}' must be a relative path without '..'");
        }
    }

    private static void ValidateInstaller(GameDefinition definition, List<string> problems)
    {
        if (definition.Installer is not { } installer) return;

        CheckTarget("installer.target", installer.Target);
        if (installer.Fallback is { } fallback) CheckTarget("installer.fallback", fallback);

        void CheckTarget(string key, string target)
        {
            // A target is either a location name or a path starting with one, "Game/Data".
            var location = target.Split('/', 2)[0];
            if (!definition.Locations.ContainsKey(location)) problems.Add($"{key} '{target}' names location '{location}', which locations doesn't declare");
        }
    }

    private void ValidateCapabilities(GameDefinition definition, List<string> problems)
    {
        foreach (var (name, block) in definition.Capabilities)
        {
            if (!capabilities.TryGet(name, out var capability))
            {
                problems.Add($"capability '{name}' is not registered; known capabilities: {string.Join(", ", capabilities.Names.Order())}");
                continue;
            }

            try
            {
                CapabilityConfig.Bind(block, capability.ConfigType);
            }
            catch (DefinitionException e)
            {
                problems.Add($"capabilities.{name} is not a valid {name} config: {e.Message}");
            }
        }
    }

    private static bool IsRelativeWithoutParent(string path)
    {
        if (path.StartsWith('/') || path.StartsWith('\\')) return false;
        // "C:\..." or "C:/...": a drive letter makes the path absolute under Wine.
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') return false;
        return !path.Split('/', '\\').Contains("..");
    }
}
