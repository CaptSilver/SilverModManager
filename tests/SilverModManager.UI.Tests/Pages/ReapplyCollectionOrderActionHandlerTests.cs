using System.Collections.Immutable;
using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Resources;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

public class ReapplyCollectionOrderActionHandlerTests(IServiceProvider provider) : AUiTest(provider)
{
    private ReapplyCollectionOrderActionHandler Handler => new(Provider, Substitute.For<IDiagnosticManager>());

    private static readonly LoadoutId Loadout = LoadoutId.From(EntityId.From(0xBEEF));

    [Fact]
    public void Test_TheButtonUsesTheLocalizedLabel()
    {
        var action = DiagnosticActions.ReapplyCollectionOrder(42);

        Handler.LabelFor(action).Should().Be(Language.DiagnosticAction_ReapplyCollectionOrder).And.NotBeNullOrWhiteSpace();
        Handler.ActionId.Should().Be(action.Id);
    }

    [Fact]
    public async Task Test_ACollectionThatIsGone_IsARefusalTheUserCanRead()
    {
        var act = () => Handler.ExecuteAsync(Loadout, DiagnosticActions.ReapplyCollectionOrder(0xDEAD).Arguments);

        (await act.Should().ThrowAsync<CollectionOrderReapplyRefusedException>()).Which.Message.Should().Contain("no longer installed");
    }

    [Fact]
    public async Task Test_AnActionWithoutACollection_IsRejected()
    {
        var act = () => Handler.ExecuteAsync(Loadout, ImmutableArray<string>.Empty);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
