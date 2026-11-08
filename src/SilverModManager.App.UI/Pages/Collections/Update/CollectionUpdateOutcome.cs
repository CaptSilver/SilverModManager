using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.App.UI.Pages.Collections.Update;

/// <summary>
/// How an applied update ended. <paramref name="Undo"/> is null when no restore point was written (Nexus), so the flow
/// offers no Undo and no implementation has to throw for one.
/// </summary>
/// <param name="DroppedToAsk">Mods the update took out of the collection that the user decides about.</param>
/// <param name="Removed">Names of mods the update removed without asking.</param>
/// <param name="ToastText">What the toast after the update says, error included.</param>
public sealed record CollectionUpdateOutcome(
    bool Updated,
    int Failed,
    int Pending,
    IReadOnlyList<DroppedMod> DroppedToAsk,
    IReadOnlyList<string> Removed,
    TxId? RestorePoint,
    Func<CancellationToken, Task>? Undo,
    Exception? Error,
    string ToastText);
