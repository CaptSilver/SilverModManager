using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// The openmw.cfg the OpenMW row writes its plugin list into is gone. Only OpenMW's launcher can make a config that
/// points at the Morrowind install, so the app does not write one from nothing.
/// </summary>
/// <remarks>A preflight refusal, so applying leaves nothing half-written.</remarks>
public sealed class OpenMwConfigMissingException(AbsolutePath folder)
    : SynchronizerPreflightException($"openmw.cfg is missing from {folder}; start the OpenMW launcher once to recreate it.")
{
    /// <inheritdoc />
    public override string Title => "OpenMW's config is missing";
}
