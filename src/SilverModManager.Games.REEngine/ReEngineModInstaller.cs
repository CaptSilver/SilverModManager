using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.REEngine;

/// <summary>
/// Installs what an RE Engine mod is made of: loose game files below <c>natives</c>, and REFramework Lua scripts and plugin
/// DLLs below <c>reframework</c>. The folder is taken from the archive root or from one wrapper folder, and goes to the
/// game folder under that name, in lower case as the game and REFramework read it on a case-sensitive file system.
/// </summary>
public sealed class ReEngineModInstaller(IServiceProvider serviceProvider)
    : ARoutedArchiveInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<ReEngineModInstaller>>())
{
    private static readonly string[] Folders = [ReFrameworkFiles.Natives, ReFrameworkFiles.Root];

    protected override string ExpectedFiles => $"files below {ReFrameworkFiles.Natives} or {ReFrameworkFiles.Root}";

    protected override bool TryRoute(IReadOnlyList<RelativePath> archive, RelativePath path, out GamePath target)
    {
        var parts = path.Parts.Select(part => part.ToString()).ToArray();

        // The folder is a directory holding the file, so the last part (the file) never counts; one wrapper folder is allowed above it.
        for (var i = 0; i < Math.Min(2, parts.Length - 1); i++)
        {
            var folder = Folders.FirstOrDefault(name => name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (folder is null) continue;

            target = new GamePath(LocationId.Game, string.Join('/', parts.Skip(i + 1).Prepend(folder)));
            return true;
        }

        target = default;
        return false;
    }
}
