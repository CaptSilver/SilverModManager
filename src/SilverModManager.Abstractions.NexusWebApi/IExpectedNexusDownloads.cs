using JetBrains.Annotations;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Abstractions.NexusWebApi;

/// <summary>
/// Files the app has sent the user to Nexus Mods for. An nxm link for one of them is accepted even when the library
/// looks like it already holds the file (it may hold only a row without the archive's bytes) or when the link's game
/// has no loadout (a list for one game can pull files from another game's domain).
/// </summary>
[PublicAPI]
public interface IExpectedNexusDownloads
{
    bool IsExpected(GameDomain domain, ModId modId, FileId fileId);
}
