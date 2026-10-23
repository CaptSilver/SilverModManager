using System.Text;
using FluentAssertions;
using SilverModManager.App.UI.Pages.Changelog;

namespace SilverModManager.UI.Tests.Pages;

public class ChangelogPageViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private const string ResourceName = "SilverModManager.App.UI.CHANGELOG.md";

    [Fact]
    public async Task Test_ActivatingShowsTheBundledChangelog()
    {
        // Read independently of the view model so a missing or renamed resource fails here.
        await using var stream = typeof(ChangelogPageViewModel).Assembly.GetManifestResourceStream(ResourceName)
                                 ?? throw new InvalidOperationException($"{ResourceName} is not embedded in the UI assembly");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var expected = await reader.ReadToEndAsync();
        expected.Should().NotBeNullOrWhiteSpace();

        // Activation binds on the UI thread, so it has to start there.
        using var wrapper = await OnUi(() => Task.FromResult(GetActivatedViewModel<IChangelogPageViewModel>()));

        await Eventually(() => wrapper.VM.MarkdownRendererViewModel.Contents.Should().Be(expected));
    }
}
