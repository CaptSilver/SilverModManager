using NexusMods.Paths;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>
/// A game described by a <see cref="SimpleGameDefinition"/>. Every simple game is an instance of this class, so
/// nothing may look one up by type.
/// </summary>
public sealed class SimpleGame(IServiceProvider provider, SimpleGameDefinition definition)
    : DefinedGame<SimpleGameDefinition>(provider, definition), IGameIdentifyingFiles, IToolVariableSource
{
    public IReadOnlyList<RelativePath> IdentifyingFiles => Definition.IdentifyingFiles;

    /// <summary>The folder mods go into is the one a tool is pointed at as its data folder.</summary>
    public IReadOnlyDictionary<string, GamePath> ToolVariables(Loadout.ReadOnly loadout) =>
        new Dictionary<string, GamePath> { [ToolVariableNames.DataFolder] = Definition.ModRoot.ToGamePath(RelativePath.Empty) };

    protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => new SimpleGameSynchronizer(provider);

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider)
    {
        var rules = Definition.Install;
        var installers = new List<ILibraryItemInstaller>();

        // First: a marker file says what the archive is, and its other files would otherwise match a folder target.
        foreach (var rule in rules.MarkerRules)
        {
            var marker = rule.MarkerFile;
            installers.Add(new PredicateBasedInstaller(provider)
            {
                Root = node => node.HasDirectChild(marker),
                Destination = Definition.ModRoot.ToGamePath(rule.Destination),
                InFolderNamedAfterMod = rule.InFolderNamedAfterMod,
                IgnoreFiles = rule.IgnoredExtensions.Cast<object>().ToArray(),
            });
        }

        if (rules.Targets.Count > 0)
        {
            installers.Add(new GenericPatternMatchInstaller(provider)
            {
                InstallFolderTargets = rules.Targets.Select(target => new InstallFolderTarget
                {
                    DestinationGamePath = Definition.ModRoot.ToGamePath(target.Destination),
                    KnownSourceFolderNames = target.KnownSourceFolderNames,
                    Names = target.KnownSubfolderNames,
                    KnownValidFileExtensions = target.KnownValidFileExtensions,
                    FileExtensionsToDiscard = target.FileExtensionsToDiscard,
                }).ToArray(),
            });
        }

        if (rules.InstallAsIs)
        {
            installers.Add(new PredicateBasedInstaller(provider)
            {
                Root = _ => true,
                Destination = Definition.ModRoot.ToGamePath(RelativePath.Empty),
            });
        }

        return installers.ToArray();
    }
}
