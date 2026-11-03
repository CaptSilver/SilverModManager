using NexusMods.Paths;

namespace SilverModManager.Games.Generic.Diagnostics;

/// <summary>
/// Where OpenXR keeps the name of the active runtime. A seam so tests never read the real home folder.
/// </summary>
public interface IOpenXrConfig
{
    /// <summary>
    /// The <c>active_runtime.json</c> file OpenXR loaders read to find the runtime.
    /// </summary>
    AbsolutePath ActiveRuntimeFile { get; }
}

/// <summary>
/// The file in <c>$XDG_CONFIG_HOME/openxr/1</c>, which is <c>~/.config/openxr/1</c> when the variable is unset.
/// </summary>
public sealed class XdgOpenXrConfig(IFileSystem fileSystem) : IOpenXrConfig
{
    /// <inheritdoc />
    public AbsolutePath ActiveRuntimeFile => fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME) / "openxr/1/active_runtime.json";
}
