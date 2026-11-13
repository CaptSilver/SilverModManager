using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// One place an OpenMW install keeps its settings: the native install's config and data folders, or the Flatpak's.
/// </summary>
/// <param name="ConfigFolder">Holds openmw.cfg and settings.cfg.</param>
/// <param name="UserDataFolder">OpenMW's user data root, the parent of the default <c>data-local</c> folder and of the saves.</param>
/// <param name="Store">Which row of My Games this root is. Native and Flatpak are separate stores so that two
/// installations over one Morrowind folder stay two installations.</param>
/// <param name="ResolveProgram">The program that starts this install, or null when it is not installed. A function
/// because the install can appear or go while the app runs.</param>
public sealed record OpenMwConfigRoot(AbsolutePath ConfigFolder, AbsolutePath UserDataFolder, GameStore Store, Func<AbsolutePath?> ResolveProgram);
