using Microsoft.Extensions.Logging;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// Finds OpenMW installs from the openmw.cfg of each config root. A root counts when it has a config, OpenMW is
/// installed for it, and the config's <c>data=</c> lines name a folder holding Morrowind.esm: the game's folder is
/// that folder's parent. Native and Flatpak are separate stores, so both can be managed over one Morrowind folder.
/// </summary>
public class OpenMwLocator(IOpenMwConfigRoots roots, IFileSystem fileSystem, IEnumerable<IGameData> games, ILogger<OpenMwLocator>? logger = null) : IGameLocator
{
    private const string Name = nameof(OpenMwLocator);

    public IEnumerable<GameLocatorResult> Locate() => Locate(NullProblemSink.Instance);

    public IEnumerable<GameLocatorResult> Locate(IGameLocatorProblemSink problems)
    {
        var game = games.FirstOrDefault(candidate => candidate.GameId == OpenMw.GameId);
        if (game is null) yield break;

        foreach (var root in roots.Roots)
        {
            var config = fileSystem.FromUnsanitizedFullPath(root.ConfigFolder.GetFullPath()) / OpenMwConstants.ConfigFileName;
            if (!config.FileExists) continue;

            if (root.ResolveProgram() is null)
            {
                Report(problems, $"openmw.cfg found at {root.ConfigFolder} but OpenMW is not installed");
                continue;
            }

            if (MorrowindDataFolder(config, root) is not { } dataFolder)
            {
                Report(problems, $"openmw.cfg found at {root.ConfigFolder} but none of its data= folders holds Morrowind.esm; run the OpenMW launcher's installation wizard first");
                continue;
            }

            yield return new GameLocatorResult
            {
                Game = game,
                Path = dataFolder.Parent,
                Store = root.Store,
                StoreIdentifier = root.ConfigFolder.GetFullPath(),
                LocatorIds = [],
                Locator = this,
            };
        }
    }

    private AbsolutePath? MorrowindDataFolder(AbsolutePath config, OpenMwConfigRoot root)
    {
        string text;
        try
        {
            text = OpenMwPaths.ReadConfig(config) ?? "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UndecodablePluginListException)
        {
            logger?.LogWarning(e, "Could not read {Config}", config);
            return null;
        }

        return OpenMwConfigValues.Values(text, "data")
            .Select(value => OpenMwPaths.Resolve(value, root, fileSystem))
            .OfType<AbsolutePath>()
            .Cast<AbsolutePath?>()
            .FirstOrDefault(folder => folder is { } candidate && (candidate / "Morrowind.esm").FileExists);
    }

    private void Report(IGameLocatorProblemSink problems, string message)
    {
        logger?.LogWarning("{Message}", message);
        problems.Report(Name, message);
    }

    private sealed class NullProblemSink : IGameLocatorProblemSink
    {
        public static readonly NullProblemSink Instance = new();
        public void Report(string locator, string message) { }
    }
}
