using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.DataModel.CommandLine.Verbs;

/// <summary>
/// Reads <c>--large-files</c>. A verb always hands the synchronizer an explicit answer: with none, a verb forwarded
/// to the running window would open a dialog on the desktop while the terminal waited for it.
/// </summary>
public static class BackupOverflowChoices
{
    /// <summary>Refuse when the option is absent, otherwise the choice it names.</summary>
    public static BackupOverflowChoice Parse(string? value)
    {
        if (value is null) return BackupOverflowChoice.Refuse;

        return value.Trim().ToLowerInvariant() switch
        {
            "adopt" => BackupOverflowChoice.AdoptInPlace,
            "backup" => BackupOverflowChoice.BackUpAnyway,
            _ => throw new InvalidLargeFilesOptionException(value),
        };
    }
}

/// <summary>A <c>--large-files</c> value that is neither <c>adopt</c> nor <c>backup</c>.</summary>
public sealed class InvalidLargeFilesOptionException(string value)
    : UserFacingException($"`--large-files {value}` is not a choice. Use `--large-files adopt` to keep the files where they are, or `--large-files backup` to copy them all first.")
{
    /// <inheritdoc />
    public override string Title => "Unknown --large-files value";
}
