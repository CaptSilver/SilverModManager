using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The open-in-editor and remove commands depend only on the page's own state, so they exist from construction
/// and stay quiet while the page has no context or selection.
/// </summary>
public class LoadoutGroupFilesViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly GamePath SomeFile = new(LocationId.Game, "meshes/rock/rock.nif");

    private LoadoutGroupFilesViewModel CreateViewModel() => new(
        NullLogger<LoadoutGroupFilesViewModel>.Instance,
        Substitute.For<IWindowManager>(),
        new ServiceCollection().BuildServiceProvider(),
        Connection);

    [Fact]
    public async Task Commands_ExistBeforeThePageIsActivated()
    {
        var viewModel = await OnUi(() => Task.FromResult(CreateViewModel()));

        viewModel.OpenEditorCommand.Should().NotBeNull();
        viewModel.RemoveCommand.Should().NotBeNull();
    }

    [Fact]
    public async Task OpenEditorCommand_WithASelectionButNoContext_CannotRun()
    {
        var viewModel = await OnUi(() => Task.FromResult(CreateViewModel()));

        await OnUi(() =>
        {
            using var activation = viewModel.Activator.Activate();
            viewModel.SelectedItem = new CompositeItemModel<GamePath>(SomeFile);

            viewModel.OpenEditorCommand.CanExecute().Should().BeFalse();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RemoveCommand_WithASelectionButNoContext_CannotRun()
    {
        var viewModel = await OnUi(() => Task.FromResult(CreateViewModel()));

        await OnUi(() =>
        {
            using var activation = viewModel.Activator.Activate();
            viewModel.SelectedItem = new CompositeItemModel<GamePath>(SomeFile);

            viewModel.RemoveCommand.CanExecute().Should().BeFalse();
            return Task.CompletedTask;
        });
    }
}
