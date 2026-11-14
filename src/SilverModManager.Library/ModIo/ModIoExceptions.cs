using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Library.ModIo;

/// <summary>
/// A downloaded mod.io file did not match the checksum mod.io lists or the digest that was pinned, or there was nothing
/// to check it against. Nothing was added to the library.
/// </summary>
[PublicAPI]
public sealed class ModIoIntegrityException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The mod.io file was rejected";
}

/// <summary>
/// The app will not install this mod: mod.io's virus scan flagged the file. <see cref="Page"/> is where the user can
/// look at it and decide.
/// </summary>
[PublicAPI]
public sealed class ModIoInstallRefusedException(string reason, Uri? page)
    : UserFacingException(page is null ? reason : $"{reason} Look at [the mod's page]({page}) and decide for yourself.")
{
    /// <summary>The mod's page on mod.io, null when the refusal is not about one mod.</summary>
    public Uri? Page { get; } = page;

    /// <inheritdoc />
    public override string Title => "The mod was not installed";
}
