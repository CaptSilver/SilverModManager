using System.Collections.Immutable;
using JetBrains.Annotations;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.App.UI.Pages.Diagnostics;

/// <summary>
/// Downloads me3 and installs it into the loadout, the same thing the app does by itself after the first mod. A failed
/// download throws, which the details page reports; the diagnostic stays until it works.
/// </summary>
[UsedImplicitly]
public sealed class InstallMe3ActionHandler(Me3HostProvisioner provisioner, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => Me3HostMissingEmitter.InstallActionId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        await provisioner.EnsureInstalled(loadoutId, CancellationToken.None);
        await diagnosticManager.RefreshAsync(loadoutId);
    }
}
