namespace SilverModManager.Networking.GameBanana.Tests.Fixtures;

/// <summary>
/// Replies captured from <c>gamebanana.com/apiv11</c> and trimmed to a few records.
/// </summary>
internal static class GameBananaJson
{
    public static string ModIndex => Read("Mod_Index_mostdownloaded.json");
    public static string Subfeed => Read("Game_Subfeed.json");
    public static string Search => Read("Util_Search_Results.json");
    public static string Profile44334 => Read("Mod_44334_ProfilePage.json");
    public static string Profile206285 => Read("Mod_206285_ProfilePage.json");
    public static string PrivateStub => Read("Mod_572595_private.json");

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
