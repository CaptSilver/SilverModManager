using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Login;
using SilverModManager.App.UI.Resources;
using SilverModManager.Sdk;

namespace SilverModManager.UI.Tests;

public class SecretPromptTests
{
    private static (SecretPrompt Prompt, Func<ApiKeyLoginOverlayViewModel> Shown) PromptWithCapturedOverlay(string? answer = "pasted")
    {
        var controller = Substitute.For<IOverlayController>();
        IOverlayViewModel<string>? shown = null;
        controller.EnqueueAndWait(Arg.Do<IOverlayViewModel<string>>(vm => shown = vm)).Returns(answer);
        return (new SecretPrompt(controller, Substitute.For<IOSInterop>()), () => (ApiKeyLoginOverlayViewModel)shown!);
    }

    [Fact]
    public async Task TheOverlayShowsTheTitleAndInstructionsItWasGiven()
    {
        var (prompt, shown) = PromptWithCapturedOverlay();

        await prompt.PromptAsync("GitHub", "Paste a token.", new Uri("https://github.com/settings/tokens"), (_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None);

        shown().Title.Value.Should().Be("GitHub");
        shown().Instructions.Value.Should().Be("Paste a token.");
        shown().PageUrl.Value.Should().Be("https://github.com/settings/tokens");
    }

    [Fact]
    public async Task ASecretWithoutAPageShowsNoLink()
    {
        var (prompt, shown) = PromptWithCapturedOverlay();

        await prompt.PromptAsync("GitHub", "Paste a token.", page: null, (_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None);

        shown().PageUrl.Value.Should().BeEmpty();
        CanExecute(shown().CommandOpenPage).Should().BeFalse();
    }

    [Fact]
    public async Task ClickingThePageLinkOpensItInTheBrowser()
    {
        var controller = Substitute.For<IOverlayController>();
        IOverlayViewModel<string>? shown = null;
        controller.EnqueueAndWait(Arg.Do<IOverlayViewModel<string>>(vm => shown = vm)).Returns("pasted");
        var osInterop = Substitute.For<IOSInterop>();
        var page = new Uri("https://steamcommunity.com/dev/apikey");

        await new SecretPrompt(controller, osInterop).PromptAsync("Steam", "Paste a key.", page, (_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None);
        ((ApiKeyLoginOverlayViewModel)shown!).CommandOpenPage.Execute(R3.Unit.Default);

        osInterop.Received(1).OpenUri(page);
    }

    [Fact]
    public async Task TheNexusKeyPageLinkOpensInTheBrowser()
    {
        var controller = Substitute.For<IOverlayController>();
        IOverlayViewModel<string>? shown = null;
        controller.EnqueueAndWait(Arg.Do<IOverlayViewModel<string>>(vm => shown = vm)).Returns("key");
        var osInterop = Substitute.For<IOSInterop>();

        await new ApiKeyPrompt(controller, osInterop).PromptAsync((_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None);
        ((ApiKeyLoginOverlayViewModel)shown!).CommandOpenPage.Execute(R3.Unit.Default);

        osInterop.Received(1).OpenUri(new Uri("https://www.nexusmods.com/users/myaccount?tab=api"));
    }

    private static bool CanExecute(System.Windows.Input.ICommand command) => command.CanExecute(null);

    [Fact]
    public async Task ThePromptReturnsWhatTheOverlayCompletedWith()
    {
        var (prompt, _) = PromptWithCapturedOverlay("ghp_pasted");

        (await prompt.PromptAsync("GitHub", "", page: null, (_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None)).Should().Be("ghp_pasted");
    }

    [Fact]
    public async Task ThePromptReturnsNullWhenTheOverlayIsCancelled()
    {
        var (prompt, _) = PromptWithCapturedOverlay(null);

        (await prompt.PromptAsync("GitHub", "", page: null, (_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TheNexusPromptKeepsTheTextsTheDialogShowedBefore()
    {
        var controller = Substitute.For<IOverlayController>();
        IOverlayViewModel<string>? shown = null;
        controller.EnqueueAndWait(Arg.Do<IOverlayViewModel<string>>(vm => shown = vm)).Returns("key");

        await new ApiKeyPrompt(controller, Substitute.For<IOSInterop>()).PromptAsync((_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None);

        var viewModel = (ApiKeyLoginOverlayViewModel)shown!;
        viewModel.Title.Value.Should().Be(Language.ApiKeyLoginOverlayView_Title);
        viewModel.Instructions.Value.Should().Be(Language.ApiKeyLoginOverlayView_Description);
        viewModel.Watermark.Value.Should().Be(Language.ApiKeyLoginOverlayView_Watermark);
        viewModel.PageUrl.Value.Should().Be("https://www.nexusmods.com/users/myaccount?tab=api");
    }
}
