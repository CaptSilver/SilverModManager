using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Library.Installers;

/// <summary>
/// Why a mod could not be installed without a person.
/// </summary>
public enum ReviewKind
{
    /// <summary>
    /// No installer knows where the files of the archive go.
    /// </summary>
    NoInstaller,

    /// <summary>
    /// A FOMOD installer has choices that only a person can make.
    /// </summary>
    NeedsChoices,
}

/// <summary>
/// An unattended install reached a mod that needs a person. Nothing was installed for the mod.
/// </summary>
public sealed class ReviewRequiredException : UserFacingException
{
    /// <summary>
    /// Creates the exception for <paramref name="modName"/>.
    /// </summary>
    public ReviewRequiredException(ReviewKind kind, string modName, string reason, Exception? innerException = null)
        : base(MessageFor(kind, modName), innerException)
    {
        Kind = kind;
        Reason = reason;
    }

    /// <summary>
    /// What kind of help the mod needs.
    /// </summary>
    public ReviewKind Kind { get; }

    /// <summary>
    /// The installer's own explanation. May be empty.
    /// </summary>
    public string Reason { get; }

    /// <inheritdoc />
    public override string Title => "Needs review";

    private static string MessageFor(ReviewKind kind, string modName) => kind switch
    {
        ReviewKind.NoInstaller => $"No installer knows where the files of {MarkdownText.Code(modName)} go.",
        ReviewKind.NeedsChoices => $"{MarkdownText.Code(modName)} has choices to make before it can be installed.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
