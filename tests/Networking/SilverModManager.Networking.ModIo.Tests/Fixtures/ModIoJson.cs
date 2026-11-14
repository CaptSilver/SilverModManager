namespace SilverModManager.Networking.ModIo.Tests.Fixtures;

/// <summary>
/// Replies written out from the object schemas in mod.io's REST reference. Every mod.io request needs a key or token,
/// so none of them was captured from the live service.
/// </summary>
internal static class ModIoJson
{
    public static string Mods => Read("Mods_List.json");
    public static string Files => Read("Files_List.json");
    public static string Me => Read("Me.json");
    public static string ReadOnlyToken => Read("Error_ReadOnlyToken.json");
    public static string TermsNotAccepted => Read("Error_TermsNotAccepted.json");
    public static string Subscribed => Read("Subscribe_201.json");

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
