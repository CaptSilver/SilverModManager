using System.Text.RegularExpressions;

namespace NexusMods.Games.FromSoft.Me3;

/// <summary>
/// Writes the co-op password into a copy of Seamless Co-op's settings file. The loadout holds the template as
/// shipped, because loadout files end up in the file store, in backups and in exported locks; the password is
/// put in only when the file is written for a session.
/// </summary>
public static partial class SeamlessCoopSettings
{
    private const string Placeholder = "@COOP_PASSWORD@";

    [GeneratedRegex(@"^cooppassword[ \t]*=[^\r\n]*", RegexOptions.Multiline)]
    private static partial Regex PasswordLine();

    /// <summary>
    /// Returns <paramref name="template"/> with <paramref name="password"/> in it: in place of the placeholder when
    /// the template has one, else on its <c>cooppassword</c> line. A password that has nowhere to go throws, since
    /// a release that renamed the key would otherwise give a session anyone can join and report success. With no
    /// password an unrecognised layout is returned unchanged, but a placeholder is still emptied so it never
    /// becomes the password itself.
    /// </summary>
    public static string Inject(string template, string? password)
    {
        password ??= "";
        if (template.Contains(Placeholder, StringComparison.Ordinal))
            return template.Replace(Placeholder, password, StringComparison.Ordinal);

        var hits = 0;
        var injected = PasswordLine().Replace(template, _ =>
        {
            hits++;
            return $"cooppassword = {password}";
        });

        if (hits == 0 && password.Length > 0)
            throw new InvalidOperationException("The Seamless Co-op settings file has no 'cooppassword' line to put the password into. The layout of the release has changed, so the password cannot be set.");

        return injected;
    }
}
