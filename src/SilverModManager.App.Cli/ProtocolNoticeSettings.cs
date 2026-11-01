using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI;

/// <summary>
/// Which app the user was last told holds each scheme, so a start that finds the same holder stays quiet.
/// </summary>
public record ProtocolNoticeSettings : ISettings
{
    /// <summary>
    /// Desktop file name by scheme, e.g. <c>ror2mm</c> to <c>r2modman.desktop</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> LastReportedHolder { get; init; } = new Dictionary<string, string>();

    public static string SettingsId => "SilverModManager.CLI.ProtocolNoticeSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder) => settingsBuilder;
}
