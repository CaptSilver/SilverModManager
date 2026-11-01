using System.Collections.Immutable;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Pages.Diagnostics.PrefixSetup;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Opens the prefix setup for the components named in the diagnostic's action arguments, through
/// <see cref="PrefixSetupLauncher"/>.
/// </summary>
[UsedImplicitly]
public sealed class SetUpPrefixActionHandler(IServiceProvider serviceProvider, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.SetUpPrefixId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        if (arguments.IsDefaultOrEmpty) throw new ArgumentException("Expected at least one component id.", nameof(arguments));

        var components = arguments.Select(id => new PrefixComponentId(id)).ToList();
        await new PrefixSetupLauncher(serviceProvider).ShowAsync(loadoutId, components);

        // The prefix is not in the database, so nothing else would rerun the emitter that raised the diagnostic.
        await diagnosticManager.RefreshAsync(loadoutId);
    }
}
