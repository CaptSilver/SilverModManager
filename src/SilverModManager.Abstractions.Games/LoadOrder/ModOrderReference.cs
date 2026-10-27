using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>How a source names a mod. Any field may be absent; matching tries them in the order declared.</summary>
public sealed record ModOrderReference(Md5Value Md5 = default, ModId? ModId = null, FileId? FileId = null, string? Name = null)
{
    /// <summary>False when nothing identifies the mod; the matcher drops such an entry instead of placing it.</summary>
    public bool IsIdentified => Md5 != default || HasIds || !string.IsNullOrWhiteSpace(Name);

    /// <summary>Both ids are needed to pick out a file; a mod id alone would match every file of the mod page.</summary>
    internal bool HasIds => ModId is not null && FileId is not null;

    /// <summary>A mod id with nothing else: it names a mod page, not a file, so it identifies nothing.</summary>
    internal bool HasOnlyModId => ModId is not null && !IsIdentified;

    /// <summary>Name, else "mod {ModId} file {FileId}", else the md5, else "mod {ModId}".</summary>
    public string Display
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name;
            if (HasIds) return $"mod {ModId!.Value.Value} file {FileId!.Value.Value}";
            if (Md5 != default) return Md5.ToString();
            return ModId is { } modId ? $"mod {modId.Value}" : "an unnamed entry";
        }
    }
}

/// <param name="Enabled">Switch the mod on (true) or off (false); null leaves it as it is.</param>
public sealed record ModOrderSourceEntry(ModOrderReference Reference, bool? Enabled);
