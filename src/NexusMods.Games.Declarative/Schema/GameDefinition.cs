namespace NexusMods.Games.Declarative.Schema;

/// <summary>
/// A game described in YAML. Property names are the camelCase keys of the file; a key that is not a
/// property here is an error when the file is read. Missing required keys are caught by
/// <see cref="DefinitionValidator"/>, since YAML deserialisation leaves them at their defaults.
/// </summary>
public sealed record GameDefinition
{
    /// <summary>Stable id, the string <c>GameId.From</c> hashes. Never renamed once a loadout exists.</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>The game's domain and id on Nexus Mods, both written out literally. Absent for a game Nexus doesn't list.</summary>
    public NexusDefinition? Nexus { get; init; }

    public StoreDefinitions Stores { get; init; } = new();

    /// <summary>The game's executable, relative to the install folder.</summary>
    public string PrimaryExe { get; init; } = "";

    /// <summary>Whether the game ships a native Linux build.</summary>
    public bool NativeLinux { get; init; }

    /// <summary>Location name to where it is. <c>Game</c> is required and is the install folder.</summary>
    public Dictionary<string, LocationDefinition> Locations { get; init; } = new();

    /// <summary>Folder names installers treat as the game's root.</summary>
    public string[] Aliases { get; init; } = [];

    /// <summary>The generic pattern installer, used when no capability supplies one.</summary>
    public InstallerDefinition? Installer { get; init; }

    public DeploymentDefinition Deployment { get; init; } = new();

    public RuntimeDefinition Runtime { get; init; } = new();

    /// <summary>
    /// Capability name to its own config block. The blocks stay as plain YAML data here; a capability
    /// types its block through <see cref="Capabilities.CapabilityConfig.Bind"/>.
    /// </summary>
    public Dictionary<string, object?> Capabilities { get; init; } = new();

    /// <summary>Extra Windows tools, by executable relative to the install folder.</summary>
    public string[] Tools { get; init; } = [];
}

public sealed record NexusDefinition
{
    public string Domain { get; init; } = "";

    public long Id { get; init; }
}

public sealed record StoreDefinitions
{
    public uint[] Steam { get; init; } = [];

    public long[] Gog { get; init; } = [];

    /// <summary>Epic Games Store catalog item ids.</summary>
    public string[] Epic { get; init; } = [];

    /// <summary>Amazon product ids.</summary>
    public string[] Amazon { get; init; } = [];

    /// <summary>The game has no store ids and is only found by being added by path.</summary>
    public bool ManualOnly { get; init; }
}

public sealed record LocationDefinition
{
    /// <summary><c>install</c> or one of the known-folder tokens, see <see cref="LocationRoots"/>.</summary>
    public string Root { get; init; } = "";

    /// <summary>Path below the root; the root itself when empty.</summary>
    public string Path { get; init; } = "";
}

public sealed record InstallerDefinition
{
    /// <summary>Folders that identify the root of a mod's data.</summary>
    public string[] DataMarkers { get; init; } = [];

    /// <summary>Where a matched mod goes: a declared location or a <c>Location/sub/folder</c> path.</summary>
    public string Target { get; init; } = "";

    /// <summary>Where unmatched files go. Without it, only the manual installer applies.</summary>
    public string? Fallback { get; init; }
}

public sealed record DeploymentDefinition
{
    /// <summary>False forces copy or link plans, for a game with a virtual file system of its own.</summary>
    public bool UsesVfs { get; init; } = true;
}

public enum ProtonRequirement
{
    Required,
    Optional,
    None,
}

public sealed record RuntimeDefinition
{
    public ProtonRequirement Proton { get; init; } = ProtonRequirement.Required;
}
