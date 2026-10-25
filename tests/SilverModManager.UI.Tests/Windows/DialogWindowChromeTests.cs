using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using FluentAssertions;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Windows;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.UI.Tests.Windows;

/// <summary>
/// Dialogs draw their own title and close button. On X11 the window manager adds a second title bar
/// unless the window opts out of system decorations, so the dialog has to move and close itself.
/// </summary>
public class DialogWindowChromeTests(IServiceProvider provider) : AUiTest(provider)
{
    private static DialogViewModel ViewModel(params DialogButtonDefinition[] buttons) => new(
        "Cleanup steps",
        buttons,
        new DialogStandardContentViewModel(new StandardDialogParameters { Text = "Some text" }),
        DialogWindowSize.Medium,
        showChrome: true);

    private static DialogWindow ShowDialog(DialogViewModel viewModel)
    {
        var window = new DialogWindow { DataContext = viewModel, Width = 480 };
        window.Show();
        return window;
    }

    [Fact]
    public async Task DialogWindow_DrawsItsOwnChrome()
    {
        await OnUi(() =>
        {
            var window = ShowDialog(ViewModel());
            window.SystemDecorations.Should().Be(SystemDecorations.None);
        });
    }

    [Fact]
    public async Task PressingTheTitleArea_StartsAMove()
    {
        await OnUi(() =>
        {
            var window = ShowDialog(ViewModel());
            var moves = 0;
            window.BeginMove = _ => moves++;

            var titlebar = window.FindControl<Control>("Titlebar")!;
            var point = titlebar.TranslatePoint(new Point(4, 4), window)!.Value;
            window.MouseDown(point, MouseButton.Left);

            moves.Should().Be(1);
        });
    }

    [Fact]
    public async Task PressingTheCloseButton_DoesNotStartAMove()
    {
        await OnUi(() =>
        {
            var window = ShowDialog(ViewModel());
            var moves = 0;
            window.BeginMove = _ => moves++;

            var close = window.FindControl<Control>("CloseButton")!;
            var point = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);

            moves.Should().Be(0);
        });
    }

    [Fact]
    public async Task PressingTheContent_DoesNotStartAMove()
    {
        await OnUi(() =>
        {
            var window = ShowDialog(ViewModel());
            var moves = 0;
            window.BeginMove = _ => moves++;

            var content = window.FindControl<Control>("ContentBorder")!;
            var point = content.TranslatePoint(new Point(2, 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);

            moves.Should().Be(0);
        });
    }

    [Fact]
    public async Task TheCloseButton_ClosesWithTheCloseResult()
    {
        await OnUi(() =>
        {
            var viewModel = ViewModel();
            var window = ShowDialog(viewModel);
            var closed = false;
            window.Closed += (_, _) => closed = true;

            var close = window.FindControl<Control>("CloseButton")!;
            var point = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);

            closed.Should().BeTrue();
            viewModel.Result.ButtonId.Should().Be(ButtonDefinitionId.CloseWindow);
        });
    }

    [Fact]
    public async Task Escape_ClosesWithTheCloseResult()
    {
        await OnUi(() =>
        {
            var viewModel = ViewModel();
            var window = ShowDialog(viewModel);
            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

            closed.Should().BeTrue();
            viewModel.Result.ButtonId.Should().Be(ButtonDefinitionId.CloseWindow);
        });
    }

    [Fact]
    public async Task Escape_WithACancelButton_ClosesWithTheCancelResult()
    {
        await OnUi(() =>
        {
            var viewModel = ViewModel(DialogStandardButtons.Cancel);
            var window = ShowDialog(viewModel);
            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

            closed.Should().BeTrue();
            viewModel.Result.ButtonId.Should().Be(DialogStandardButtons.Cancel.Id);
        });
    }
}
