using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Login;
using SilverModManager.App.UI.Resources;

namespace SilverModManager.UI.Tests;

public class SecretPromptTests
{
    private static (SecretPrompt Prompt, Func<ApiKeyLoginOverlayViewModel> Shown) PromptWithCapturedOverlay(string? answer = "pasted")
    {
        var controller = Substitute.For<IOverlayController>();
        IOverlayViewModel<string>? shown = null;
        controller.EnqueueAndWait(Arg.Do<IOverlayViewModel<string>>(vm => shown = vm)).Returns(answer);
        return (new SecretPrompt(controller), () => (ApiKeyLoginOverlayViewModel)shown!);
    }

    [Fact]
    public async Task TheOverlayShowsTheTitleAndInstructionsItWasGiven()
    {
        var (prompt, shown) = PromptWithCapturedOverlay();

        await prompt.PromptAsync("GitHub", "Paste a token.", (_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None);

        shown().Title.Value.Should().Be("GitHub");
        shown().Instructions.Value.Should().Be("Paste a token.");
        shown().PageUrl.Value.Should().BeEmpty("a generic secret has no page of its own to link");
    }

    [Fact]
    public async Task ThePromptReturnsWhatTheOverlayCompletedWith()
    {
        var (prompt, _) = PromptWithCapturedOverlay("ghp_pasted");

        (await prompt.PromptAsync("GitHub", "", (_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None)).Should().Be("ghp_pasted");
    }

    [Fact]
    public async Task ThePromptReturnsNullWhenTheOverlayIsCancelled()
    {
        var (prompt, _) = PromptWithCapturedOverlay(null);

        (await prompt.PromptAsync("GitHub", "", (_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TheNexusPromptKeepsTheTextsTheDialogShowedBefore()
    {
        var controller = Substitute.For<IOverlayController>();
        IOverlayViewModel<string>? shown = null;
        controller.EnqueueAndWait(Arg.Do<IOverlayViewModel<string>>(vm => shown = vm)).Returns("key");

        await new ApiKeyPrompt(controller).PromptAsync((_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None);

        var viewModel = (ApiKeyLoginOverlayViewModel)shown!;
        viewModel.Title.Value.Should().Be(Language.ApiKeyLoginOverlayView_Title);
        viewModel.Instructions.Value.Should().Be(Language.ApiKeyLoginOverlayView_Description);
        viewModel.Watermark.Value.Should().Be(Language.ApiKeyLoginOverlayView_Watermark);
        viewModel.PageUrl.Value.Should().Be("https://www.nexusmods.com/users/myaccount?tab=api");
    }
}
