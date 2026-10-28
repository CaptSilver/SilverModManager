using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

public class CollectionLoadoutPageFactoryTests(IServiceProvider provider) : AUiTest(provider)
{
    /// <summary>
    /// A saved workspace can hold a tab for a collection the user has since removed; restoring it must say the page
    /// is gone rather than fail like a broken page.
    /// </summary>
    [Fact]
    public void ARemovedCollectionsPageIsGone()
    {
        var factory = new CollectionLoadoutPageFactory(Provider);
        var context = new CollectionLoadoutPageContext
        {
            LoadoutId = LoadoutId.From(EntityId.From(0x200000000000001)),
            GroupId = CollectionGroupId.From(EntityId.From(0x200000000FFFFFF)),
        };

        var create = () => factory.CreateViewModel(context);

        create.Should().Throw<PageTargetGoneException>();
    }
}
