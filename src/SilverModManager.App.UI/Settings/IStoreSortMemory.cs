using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Settings;

/// <summary>
/// Remembers which sort a store page was left on, per game, so reopening the page shows the same order.
/// </summary>
public interface IStoreSortMemory
{
    /// <summary>The key last saved for the page and game, or null when none was.</summary>
    string? Recall(string page, string game);

    void Remember(string page, string game, string sortKey);
}

/// <inheritdoc/>
public sealed class SettingsStoreSortMemory(ISettingsManager settings) : IStoreSortMemory
{
    public string? Recall(string page, string game)
        => settings.Get<StoreSortSettings>().Choices.TryGetValue(KeyOf(page, game), out var key) ? key : null;

    public void Remember(string page, string game, string sortKey)
    {
        var entry = KeyOf(page, game);
        if (Recall(page, game) == sortKey) return;
        settings.Update<StoreSortSettings>(s => s with { Choices = s.Choices.SetItem(entry, sortKey) });
    }

    private static string KeyOf(string page, string game) => $"{page}/{game}";
}
