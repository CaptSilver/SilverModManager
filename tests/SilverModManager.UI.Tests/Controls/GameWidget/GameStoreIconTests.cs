using FluentAssertions;
using NexusMods.App.UI.Controls.GameWidget;
using NexusMods.Sdk.Games;
using NexusMods.UI.Sdk.Icons;

namespace NexusMods.UI.Tests.Controls.GameWidget;

public class GameStoreIconTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public void SupportedStoresHaveTheirOwnIcon()
    {
        GameWidgetViewModel.MapGameStoreToIcon(GameStore.Steam).Should().Be(IconValues.Steam);
        GameWidgetViewModel.MapGameStoreToIcon(GameStore.GOG).Should().Be(IconValues.GOG);
        GameWidgetViewModel.MapGameStoreToIcon(GameStore.EGS).Should().Be(IconValues.Epic);
    }

    /// <summary>
    /// Stores the app can't locate games for (names that older databases may still carry) get the help icon.
    /// </summary>
    [Theory]
    [InlineData("Xbox Game Pass")]
    [InlineData("Origin")]
    [InlineData("EA Desktop")]
    public void UnsupportedStoresFallBackToHelpIcon(string storeName)
    {
        GameWidgetViewModel.MapGameStoreToIcon(GameStore.From(storeName)).Should().Be(IconValues.Help);
    }
}
