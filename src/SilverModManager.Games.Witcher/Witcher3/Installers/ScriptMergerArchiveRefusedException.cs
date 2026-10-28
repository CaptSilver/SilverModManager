using SilverModManager.Sdk.Errors;

namespace SilverModManager.Games.Witcher.Witcher3.Installers;

/// <summary>Raised when Witcher Script Merger is added by hand: the app merges scripts itself on Apply, so the tool is not installed.</summary>
public sealed class ScriptMergerArchiveRefusedException(string archiveName)
    : UserFacingException($"{archiveName} is Witcher Script Merger. This app merges scripts itself on Apply, so Script Merger is not needed and is not installed.")
{
    public override string Title => "Script Merger is not needed";
}
