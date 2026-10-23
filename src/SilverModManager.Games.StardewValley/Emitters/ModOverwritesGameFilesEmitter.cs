using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Diagnostics.Values;

using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.StardewValley.Emitters;

public class ModOverwritesGameFilesEmitter : ILoadoutDiagnosticEmitter
{
    private static readonly NamedLink SMAPIWikiLink = new("SMAPI Wiki", new Uri("https://stardewvalleywiki.com/Modding:Using_XNB_mods"));
    private static readonly NamedLink SMAPIWikiTableLink = new("SMAPI Wiki", new Uri("https://stardewvalleywiki.com/Modding:Using_XNB_mods#Alternatives_using_Content_Patcher"));

    private static readonly GamePath ContentDirectoryPath = new(LocationId.Game, Constants.ContentFolder);

    public async IAsyncEnumerable<Diagnostic> Diagnose(
        Loadout.ReadOnly loadout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var groups = LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .GetEnabledLoadoutFiles()
            .Where(file =>
            {
                var loadoutItem = file.AsLoadoutItemWithTargetPath().AsLoadoutItem();
                if (loadoutItem.ParentId == default(LoadoutItemGroupId)) return false;
                return !loadoutItem.Parent.TryGetAsLoadoutGameFilesGroup(out _);
            })
            .Where(file => ((GamePath)file.AsLoadoutItemWithTargetPath().TargetPath).StartsWith(ContentDirectoryPath))
            .Select(file => file.AsLoadoutItemWithTargetPath().AsLoadoutItem().Parent)
            .DistinctBy(item => item.Id);

        foreach (var group in groups)
        {
            yield return Diagnostics.CreateModOverwritesGameFiles(
                Group: group.ToReference(loadout),
                GroupName: group.AsLoadoutItem().Name,
                SMAPIWikiLink: SMAPIWikiLink,
                SMAPIWikiTableLink: SMAPIWikiTableLink
            );
        }
    }
}
