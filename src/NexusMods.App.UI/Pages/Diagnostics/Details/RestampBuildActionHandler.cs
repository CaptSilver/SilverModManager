using System.Collections.Immutable;
using JetBrains.Annotations;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Games.FromSoft.Build;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.App.UI.Pages.Diagnostics;

/// <summary>
/// Takes the build of Elden Ring that is installed now as the one future launches are compared with, once the user
/// has looked at what changed. The stamp is only a reference, so there is nothing to confirm.
/// </summary>
[UsedImplicitly]
public sealed class RestampBuildActionHandler(BuildStampStore stamps, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => BuildDriftEmitter.RestampActionId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        await stamps.Restamp(loadoutId);
        await diagnosticManager.RefreshAsync(loadoutId);
    }
}
