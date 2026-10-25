using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Settings;

namespace SilverModManager.UI.Tests.Pages;

public class StoreSortMemoryTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public void ASavedSort_IsRecalledForTheSamePageAndGameOnly()
    {
        var memory = Provider.GetRequiredService<IStoreSortMemory>();
        var game = $"game-{Guid.NewGuid()}";

        memory.Recall("thunderstore", game).Should().BeNull();
        memory.Remember("thunderstore", game, "Name");

        memory.Recall("thunderstore", game).Should().Be("Name");
        memory.Recall("nexus-mods", game).Should().BeNull();
        memory.Recall("thunderstore", game + "-other").Should().BeNull();
    }

    [Fact]
    public void ARememberedSort_OverwritesTheEarlierOne()
    {
        var memory = Provider.GetRequiredService<IStoreSortMemory>();
        var game = $"game-{Guid.NewGuid()}";

        memory.Remember("nexus-mods", game, "Downloads");
        memory.Remember("nexus-mods", game, "Updated");

        memory.Recall("nexus-mods", game).Should().Be("Updated");
    }
}
