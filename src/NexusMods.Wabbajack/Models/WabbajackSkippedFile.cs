using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;

namespace NexusMods.Wabbajack.Models;

/// <summary>Why a directive of a modlist was not executed.</summary>
public enum SkipKind : byte
{
    /// <summary>The archive the directive reads was not available.</summary>
    MissingArchive = 0,

    /// <summary>The archive comes from a source this build cannot read.</summary>
    UnsupportedSource = 1,

    /// <summary>The game files on disk are not the version the list was made for.</summary>
    GameFileVersion = 2,

    /// <summary>The archive has no member at the path the directive names.</summary>
    MemberNotFound = 3,

    /// <summary>The file the directive produced does not hash to the value the list records.</summary>
    HashMismatch = 4,

    /// <summary>The directive patches, recompresses or builds a file and this build cannot do that yet.</summary>
    Transform = 5,
}

/// <summary>
/// A directive the install could not execute. The list is left disabled while any of these exist for its group.
/// </summary>
[PublicAPI]
public partial class WabbajackSkippedFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Wabbajack.SkippedFile";

    /// <summary>The modlist group whose install skipped the file.</summary>
    public static readonly ReferenceAttribute<WabbajackModlistLoadoutGroup> Group = new(Namespace, nameof(Group)) { IsIndexed = true };

    /// <summary>The directive's destination in the MO2 instance, as the list spells it.</summary>
    public static readonly StringAttribute To = new(Namespace, nameof(To));

    public static readonly StringAttribute Reason = new(Namespace, nameof(Reason));

    public static readonly EnumByteAttribute<SkipKind> Kind = new(Namespace, nameof(Kind));
}
