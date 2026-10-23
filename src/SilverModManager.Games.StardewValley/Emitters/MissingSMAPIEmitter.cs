using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Games.StardewValley.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.StardewValley.Emitters;

public class MissingSMAPIEmitter : ILoadoutDiagnosticEmitter
{
    private readonly IGameDomainToGameIdMappingCache _mappingCache;
    
    public MissingSMAPIEmitter(IGameDomainToGameIdMappingCache mappingCache)
    {
        _mappingCache = mappingCache;
    }

    public async IAsyncEnumerable<Diagnostic> Diagnose(
        Loadout.ReadOnly loadout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var numEnabledSMAPIManifests = SMAPIManifestLoadoutFile.GetAllInLoadout(loadout.Db, loadout, onlyEnabled: true).Count();
        if (numEnabledSMAPIManifests == 0) yield break;

        var smapiLoadoutItems = LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemGroup().OfTypeSMAPILoadoutItem().ToArray();
        if (smapiLoadoutItems.Length == 0)
        {
            yield return Diagnostics.CreateSMAPIRequiredButNotInstalled(
                ModCount: numEnabledSMAPIManifests,
                NexusModsSMAPIUri: Helpers.GetSMAPILink(_mappingCache)
            );

            yield break;
        }

        var isSMAPIEnabled = smapiLoadoutItems.Any(x => x.AsLoadoutItemGroup().AsLoadoutItem().IsEnabled());
        if (isSMAPIEnabled) yield break;

        yield return Diagnostics.CreateSMAPIRequiredButDisabled(
            ModCount: numEnabledSMAPIManifests
        );
    }
}
