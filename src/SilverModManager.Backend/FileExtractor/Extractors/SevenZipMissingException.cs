using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Backend.FileExtractor.Extractors;

/// <summary>
/// The package has no 7-Zip executable for this CPU, so no archive can be extracted.
/// </summary>
public sealed class SevenZipMissingException(IReadOnlyList<string> pathsTried) : UserFacingException(
    $"The 7-Zip executable for this CPU ({BundledNativeLibrary.RuntimeIdentifiers()[^1]}) is not in the package. Looked in: {string.Join(", ", pathsTried.Select(MarkdownText.Code))}.")
{
    /// <summary>Every path that was checked, in order.</summary>
    public IReadOnlyList<string> PathsTried { get; } = pathsTried;

    /// <inheritdoc />
    public override string Title => "7-Zip is missing";
}
