using System.Text;
using NexusMods.Paths;
using SteamKit2;

namespace SilverModManager.Platform.Proton.Steam.Shortcuts;

/// <summary>
/// A Steam login with a library folder on this machine.
/// </summary>
/// <param name="AccountId">The 32-bit id Steam names the <c>userdata</c> folder after.</param>
public sealed record SteamAccount(uint AccountId, string AccountName, string PersonaName);

/// <summary>
/// The outcome of picking the account whose library gets the shortcut.
/// </summary>
public abstract record SteamAccountChoice
{
    public sealed record Chosen(SteamAccount Account) : SteamAccountChoice;

    /// <summary>More than one account could own the shortcut and nothing says which.</summary>
    public sealed record Ambiguous(IReadOnlyList<SteamAccount> Candidates) : SteamAccountChoice;

    public sealed record None(string Reason) : SteamAccountChoice;
}

/// <summary>
/// Finds which Steam account's <c>userdata</c> folder holds the shortcuts a user sees.
/// </summary>
public static class SteamAccounts
{
    private const ulong SteamId64Base = 76561197960265728;

    /// <summary>
    /// Where an account's per-user config (<c>shortcuts.vdf</c>, <c>localconfig.vdf</c>, <c>grid/</c>) lives.
    /// </summary>
    public static AbsolutePath ConfigDirectory(AbsolutePath steamRoot, uint accountId)
        => steamRoot.Combine("userdata").Combine(accountId.ToString()).Combine("config");

    /// <summary>
    /// Every login in <c>config/loginusers.vdf</c> that has a config folder to write into, in file order.
    /// </summary>
    public static IReadOnlyList<SteamAccount> Candidates(AbsolutePath steamRoot) => Read(steamRoot).Select(u => u.Account).ToList();

    /// <summary>
    /// <paramref name="requested"/> when it is one of them (an id with no config folder is passed over); otherwise the account Steam signs in as
    /// (<c>AutoLoginUser</c> in <paramref name="registryVdf"/>), else the single <c>MostRecent</c> login, else the
    /// single <c>AutoLogin</c> one. With none of those and more than one login it is ambiguous. There is no "newest
    /// timestamp" fallback: it never ties, so it would silently pick, and a shortcut in the wrong account's library
    /// is invisible to the user.
    /// </summary>
    public static SteamAccountChoice Pick(AbsolutePath steamRoot, AbsolutePath? registryVdf, uint? requested = null)
    {
        var users = Read(steamRoot);
        if (users.Count == 0) return new SteamAccountChoice.None($"No Steam account with a config folder was found under {steamRoot}.");

        if (requested is { } id && users.FirstOrDefault(u => u.Account.AccountId == id) is { } wanted) return new SteamAccountChoice.Chosen(wanted.Account);

        if (AutoLoginUser(registryVdf) is { } name && users.FirstOrDefault(u => string.Equals(u.Account.AccountName, name, StringComparison.Ordinal)) is { } signedIn)
        {
            return new SteamAccountChoice.Chosen(signedIn.Account);
        }

        if (Single(users, u => u.MostRecent) is { } recent) return new SteamAccountChoice.Chosen(recent.Account);
        if (Single(users, u => u.AutoLogin) is { } auto) return new SteamAccountChoice.Chosen(auto.Account);

        return users.Count == 1
            ? new SteamAccountChoice.Chosen(users[0].Account)
            : new SteamAccountChoice.Ambiguous(users.Select(u => u.Account).ToList());
    }

    private sealed record LoginUser(SteamAccount Account, bool MostRecent, bool AutoLogin);

    private static LoginUser? Single(IReadOnlyList<LoginUser> users, Func<LoginUser, bool> predicate)
    {
        var matches = users.Where(predicate).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static List<LoginUser> Read(AbsolutePath steamRoot)
    {
        var file = steamRoot.Combine("config").Combine("loginusers.vdf");
        var root = ReadText(file);
        var users = new List<LoginUser>();
        if (root is null) return users;

        foreach (var user in root.Children)
        {
            if (!ulong.TryParse(user.Name, out var steamId) || steamId < SteamId64Base) continue;
            var accountId = (uint)(steamId - SteamId64Base);
            if (!ConfigDirectory(steamRoot, accountId).DirectoryExists()) continue;

            var account = new SteamAccount(accountId, user["AccountName"].Value ?? "", user["PersonaName"].Value ?? "");
            users.Add(new LoginUser(account, user["MostRecent"].Value == "1", user["AutoLogin"].Value == "1"));
        }
        return users;
    }

    private static string? AutoLoginUser(AbsolutePath? registryVdf)
    {
        if (registryVdf is not { } file || ReadText(file) is not { } root) return null;
        var name = root["HKLM"]["Software"]["Valve"]["Steam"]["AutoLoginUser"].Value;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static KeyValue? ReadText(AbsolutePath file)
    {
        if (!file.FileExists) return null;
        try { return KeyValue.LoadFromString(Encoding.UTF8.GetString(File.ReadAllBytes(file.ToString()))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
