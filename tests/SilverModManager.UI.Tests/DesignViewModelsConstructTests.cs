using System.Reflection;
using FluentAssertions;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Overlays.Updater;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The view models the previewer builds by hand. A member left as <c>null</c> there only fails when a view reads it.
/// </summary>
public class DesignViewModelsConstructTests(IServiceProvider provider) : AUiTest(provider)
{
    private static IEnumerable<Type> DesignViewModelTypes() => typeof(IOverlayController).Assembly.GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && type.Name.EndsWith("DesignViewModel", StringComparison.Ordinal))
        .Where(type => type.GetConstructor(Type.EmptyTypes) is not null);

    [Fact]
    public async Task EveryDesignViewModel_ConstructsWithoutDereferencingNull()
    {
        var types = DesignViewModelTypes().ToList();
        types.Should().NotBeEmpty();

        await OnUi(() =>
        {
            foreach (var type in types)
            {
                var act = () => Activator.CreateInstance(type);
                act.Should().NotThrow<ArgumentNullException>(type.FullName);
                act.Should().NotThrow<NullReferenceException>(type.FullName);
            }
        });
    }

    [Fact]
    public void MessageBoxOkDesignViewModel_HasAnActivatorControllerAndCompletionTask()
    {
        var viewModel = new MessageBoxOkDesignViewModel();

        viewModel.Activator.Should().NotBeNull();
        viewModel.Controller.Should().NotBeNull();
        viewModel.CompletionTask.Should().NotBeNull();
    }

    [Fact]
    public async Task LoadoutLeftMenuDesignViewModel_HasANewCollectionItem()
    {
        await OnUi(() => new LoadoutLeftMenuDesignViewModel().LeftMenuItemNewCollection.Should().NotBeNull());
    }

    [Fact]
    public async Task UpdaterDesignView_BuildsItsCardsWithoutRealServices()
    {
        await OnUi(() => new UpdaterDesignView().Should().NotBeNull());
    }

    [Fact]
    public void DesignOverlayController_RefusesToDoRealWork()
    {
        var controller = new DesignOverlayController();

        controller.Invoking(c => c.Enqueue(new MessageBoxOkDesignViewModel())).Should().Throw<NotSupportedException>().WithMessage("*design-time only*");
        controller.Invoking(c => _ = c.CurrentOverlay).Should().Throw<NotSupportedException>();
    }
}
