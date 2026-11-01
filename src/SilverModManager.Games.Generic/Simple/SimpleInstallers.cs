using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using NexusMods.Paths;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>
/// The archive installers a game gets from <see cref="SimpleInstallRules"/> and the folder it reads mods from. Shared
/// by every family of games whose mods are laid out by rule, so each rule is turned into an installer in one place.
/// </summary>
public static class SimpleInstallers
{
    public static ILibraryItemInstaller[] Create(IServiceProvider provider, ModLocation modRoot, SimpleInstallRules rules)
    {
        var installers = new List<ILibraryItemInstaller>();

        // Before everything else: an archive the game turns away must not be taken by a rule that accepts any layout.
        if (rules.Refusals.Count > 0)
            installers.Add(new RefusingInstaller(provider, rules.Refusals));

        // First: a marker file says what the archive is, and its other files would otherwise match a folder target.
        foreach (var rule in rules.MarkerRules)
        {
            var marker = rule.MarkerFile;
            installers.Add(new PredicateBasedInstaller(provider)
            {
                Root = node => node.HasDirectChild(marker),
                Destination = modRoot.ToGamePath(rule.Destination),
                InFolderNamedAfterMod = rule.InFolderNamedAfterMod,
                MarkerFile = marker,
                FolderNameFromMarker = rule.FolderNameFromMarker,
                IgnoreFiles = rule.IgnoredExtensions.Cast<object>().ToArray(),
            });
        }

        foreach (var rule in rules.DescriptorRules)
        {
            var extension = rule.Extension;
            installers.Add(new PredicateBasedInstaller(provider)
            {
                Root = node => node.HasDirectChildEndingIn(extension.ToString()),
                Destination = modRoot.ToGamePath(rule.Destination),
                InFolderNamedAfterFileWithExtension = extension,
                IgnoreFiles = rule.IgnoredExtensions.Cast<object>().ToArray(),
            });
        }

        if (rules.Targets.Count > 0)
        {
            installers.Add(new GenericPatternMatchInstaller(provider)
            {
                InstallFolderTargets = rules.Targets.Select(target => new InstallFolderTarget
                {
                    DestinationGamePath = modRoot.ToGamePath(target.Destination),
                    KnownSourceFolderNames = target.KnownSourceFolderNames,
                    Names = target.KnownSubfolderNames,
                    KnownValidFileExtensions = target.KnownValidFileExtensions,
                    FileExtensionsToDiscard = target.FileExtensionsToDiscard,
                }).ToArray(),
            });
        }

        if (rules.ExtensionRoutes.Count > 0)
            installers.Add(new ExtensionRoutedInstaller(provider, modRoot, rules.ExtensionRoutes, rules.LeaveOutUnroutedFiles));

        if (rules.InstallAsIs)
        {
            installers.Add(new PredicateBasedInstaller(provider)
            {
                Root = _ => true,
                Destination = modRoot.ToGamePath(rules.InstallAsIsDestination),
            });
        }

        return installers.ToArray();
    }
}
