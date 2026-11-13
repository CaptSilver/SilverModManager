using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// A Health Check finding the player told one loadout to stop reporting. A diagnostic is keyed by its id and a subject
/// string the emitter chooses (for native code, the file's path); the content identity says which bytes were
/// ignored, so other bytes at the same path are reported again.
/// </summary>
[PublicAPI]
public partial class DiagnosticDismissal : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.DiagnosticDismissal";

    /// <summary>The loadout that stops reporting the finding.</summary>
    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };

    /// <summary>The source of the dismissed diagnostic's id.</summary>
    public static readonly StringAttribute Source = new(Namespace, nameof(Source));

    /// <summary>The number of the dismissed diagnostic's id.</summary>
    public static readonly UInt16Attribute Number = new(Namespace, nameof(Number));

    /// <summary>What the finding is about, as the emitter spells it: <c>Game|Data/SKSE/Plugins/x.dll</c> for a file.</summary>
    public static readonly StringAttribute Subject = new(Namespace, nameof(Subject));

    /// <summary>
    /// The content of the subject when it was ignored, as <c>XxHash3:&lt;hex digest&gt;</c>. Absent for a finding that
    /// has no content to compare, which then covers the subject whatever it holds.
    /// </summary>
    public static readonly StringAttribute Identity = new(Namespace, nameof(Identity)) { IsOptional = true };

    /// <summary>
    /// The diagnostic's title when it was ignored. There is no lookup from an id to its template, and the ignored
    /// list on the Health Check page shows what each row was about.
    /// </summary>
    public static readonly StringAttribute Title = new(Namespace, nameof(Title));
}
