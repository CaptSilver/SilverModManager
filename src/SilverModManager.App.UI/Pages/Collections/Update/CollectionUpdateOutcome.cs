using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.App.UI.Pages.Collections.Update;

/// <summary>
/// How an applied update ended. <paramref name="Undo"/> is null when no restore point was written (Nexus), so the flow
/// offers no Undo and no implementation has to throw for one.
/// </summary>
/// <param name="DroppedToAsk">Mods the update took out of the collection that the user decides about.</param>
/// <param name="Removed">Names of mods the update removed without asking.</param>
/// <param name="ToastText">What the toast after the update says, error included, as Markdown with names and messages escaped. The flow converts it to plain text where it shows it outside a toast.</param>
/// <param name="Warnings">How many things the update left undone without failing (rules that could not be carried over). Any makes the toast a failure one even though the update went through.</param>
/// <param name="DroppedPromptRevision">When set, the question about <paramref name="DroppedToAsk"/> names this numbered revision ("aren't in revision 2") instead of the update's new version text. A refused update sets it to the revision the collection is still on.</param>
public sealed record CollectionUpdateOutcome(
    bool Updated,
    int Failed,
    int Pending,
    IReadOnlyList<DroppedMod> DroppedToAsk,
    IReadOnlyList<string> Removed,
    TxId? RestorePoint,
    Func<CancellationToken, Task>? Undo,
    Exception? Error,
    string ToastText,
    int Warnings = 0,
    int? DroppedPromptRevision = null);
