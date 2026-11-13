using System.Collections.Immutable;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics;

/// <summary>
/// Stops a loadout reporting the subjects a finding names, by writing a <see cref="DiagnosticDismissal"/> for each.
/// A restore point comes first, so reverting to it brings the findings back. Asks nothing: the button's note says what
/// it does and the Health Check page lists what is ignored.
/// </summary>
[UsedImplicitly]
public sealed class DismissDiagnosticSubjectsActionHandler(IConnection connection, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.DismissDiagnosticSubjectsId;

    /// <inheritdoc />
    public bool RunsUnattended => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var (id, title, subjects) = DiagnosticActions.ParseDismissDiagnosticSubjects(arguments);

        await LoadoutSnapshot.WriteRestorePoint(connection, loadoutId);

        var existing = DiagnosticDismissals.For(connection.Db, loadoutId, id).ToHashSet();
        using var tx = connection.BeginTransaction();
        var written = false;
        foreach (var (subject, identity) in subjects)
        {
            if (!existing.Add(new DiagnosticDismissals.Dismissed(subject, identity))) continue;
            _ = new DiagnosticDismissal.New(tx)
            {
                LoadoutId = loadoutId,
                Source = id.Source,
                Number = id.Number,
                Subject = subject,
                Identity = identity,
                Title = title,
            };
            written = true;
        }
        if (written) await tx.Commit();

        await diagnosticManager.RefreshAsync(loadoutId);
    }
}
