using System.Runtime.CompilerServices;
using System.Text;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Generic.Diagnostics;

/// <summary>
/// Reports the prefix components a game declares through <see cref="IWinePrefixRequirements"/> that the prefix
/// does not have. It clears once the inspector finds them.
/// </summary>
/// <remarks>
/// Works for every kind of prefix the resolver knows, not only Steam's. winetricks.log is read as a hint to show
/// next to a missing component and never as proof either way. A component the inspector could not verify is listed
/// but left out of the setup action, since installing on a guess could overwrite a working install.
/// </remarks>
public class WinePrefixComponentsEmitter(IPrefixResolver prefixResolver, IPrefixInspector prefixInspector) : ILoadoutDiagnosticEmitter
{
    /// <inheritdoc />
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var installation = loadout.InstallationInstance;
        var game = installation.Game;

        var required = WinePrefixRequirements.For(game, loadout);
        if (required.Count == 0) yield break;

        var resolution = prefixResolver.Resolve(installation);
        if (!resolution.IsResolved) yield break;

        var statuses = Inspect(resolution.Prefix, required);
        var missing = statuses.Where(s => s.State == PrefixComponentState.Missing).ToList();
        if (missing.Count == 0) yield break;

        var unverified = statuses.Where(s => s.State == PrefixComponentState.Unknown).ToList();
        var hinted = await WinetricksHints(installation, cancellationToken);

        var missingList = string.Join('\n', missing.Select(s => Describe(s, hinted)));
        var unverifiedList = unverified.Count == 0
            ? ""
            : "\nThe app could not verify these:\n\n" + string.Join('\n', unverified.Select(s => Describe(s, hinted))) + "\n";
        var instructions = WineDiagnosticHelper.WinetricksInstructions(resolution.Prefix.Kind == CompatibilityLayerKind.SteamProton, missing.Select(s => s.Id.Value));

        yield return Diagnostics.CreateRequiredPrefixComponents(game.DisplayName, missingList, unverifiedList, instructions)
            with { Actions = [DiagnosticActions.SetUpPrefix(missing.Select(s => s.Id.Value))] };
    }

    /// <summary>
    /// Asks the inspector; a prefix the app cannot read marks every component unknown, so the diagnostic does not
    /// turn into an error for a folder it has no access to.
    /// </summary>
    private IReadOnlyList<PrefixComponentStatus> Inspect(ResolvedPrefix prefix, IReadOnlyList<PrefixComponentId> required)
    {
        try
        {
            return prefixInspector.Inspect(prefix, required);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return required
                .Select(id => new PrefixComponentStatus(id, PrefixComponentState.Unknown, $"could not read the prefix: {e.Message}"))
                .ToList();
        }
    }

    private static async ValueTask<IReadOnlySet<string>> WinetricksHints(GameInstallation installation, CancellationToken cancellationToken)
    {
        var provider = installation.LocatorResult.LinuxCompatabilityDataProvider;
        if (provider is null) return new HashSet<string>();
        return await provider.GetInstalledWinetricksComponents(cancellationToken);
    }

    private static string Describe(PrefixComponentStatus status, IReadOnlySet<string> winetricksHints)
    {
        var sb = new StringBuilder("* `").Append(status.Id).Append("`: ").Append(status.Evidence);
        if (status.State == PrefixComponentState.Missing && winetricksHints.Contains(status.Id.Value))
            sb.Append(" (winetricks.log lists it, but its files are not there)");
        return sb.ToString();
    }
}
