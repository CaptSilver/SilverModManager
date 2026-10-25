using SilverModManager.App.UI.Settings;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// A sort memory of its own, so a test that picks a sort doesn't leave it behind for the next test's page to restore.
/// </summary>
internal sealed class InMemoryStoreSortMemory : IStoreSortMemory
{
    private readonly Dictionary<string, string> _keys = new();

    public string? Recall(string page, string game) => _keys.GetValueOrDefault($"{page}/{game}");

    public void Remember(string page, string game, string sortKey) => _keys[$"{page}/{game}"] = sortKey;
}
