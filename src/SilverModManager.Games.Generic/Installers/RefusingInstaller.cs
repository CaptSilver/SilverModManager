using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Paths.Trees.Traits;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Generic.Installers;

/// <summary>
/// Throws a <see cref="ModNotInstallableException"/> for an archive a <see cref="SimpleRefusalRule"/> matches and
/// declines every other. Declining is not enough for these archives: the next installer would take them, and the
/// player would get a mod that does nothing.
/// </summary>
public sealed class RefusingInstaller(IServiceProvider serviceProvider, IReadOnlyList<SimpleRefusalRule> rules)
    : ALibraryArchiveInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<RefusingInstaller>>())
{
    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var paths = LibraryArchiveTreeExtensions.GetTree(libraryArchive).GetFiles().Select(file => file.Item.Path).ToArray();

        foreach (var rule in rules)
        {
            if (paths.Any(path => Matches(rule, path)))
                throw new ModNotInstallableException(rule.Reason);
        }

        return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: "Nothing in the archive is refused."));
    }

    private static bool Matches(SimpleRefusalRule rule, RelativePath path)
    {
        if (rule.FileName is { } file && path.FileName.ToString().Equals(file, StringComparison.OrdinalIgnoreCase))
            return true;

        return rule.FolderName is { } folder
            && path.Parts.SkipLast(1).Any(part => part.ToString().Equals(folder, StringComparison.OrdinalIgnoreCase));
    }
}
