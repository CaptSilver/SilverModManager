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

    [Fact]
    public async Task Test_AVersionWithoutReleaseNotesShowsTheFullChangelog()
    {
        using var wrapper = await OnUi(() => Task.FromResult(GetActivatedViewModel<IChangelogPageViewModel>()));
        var viewModel = (ChangelogPageViewModel)wrapper.VM;
        await Eventually(() => viewModel.ParsedChangelog.Should().NotBeNull());
        var full = viewModel.MarkdownRendererViewModel.Contents;

        // The bundled changelog has no version headings yet, so the page is given one that does.
        var parsed = ParsedChangelog.Parse("# v1.0.0 - first\nFirst notes\n# v2.0.0 - second\nSecond notes\n");
        var known = parsed.Versions[0].Key;
        var section = parsed.GetVersionSection(known)!;

        await OnUi(() => { viewModel.ParsedChangelog = parsed; viewModel.TargetVersion = known; });
        await Eventually(() => viewModel.SelectedIndex.Should().Be(1));
        viewModel.MarkdownRendererViewModel.Contents.Should().Be(section).And.NotBe(full);

        await OnUi(() => { viewModel.TargetVersion = new Version(999, 0, 0); });

        await Eventually(() => viewModel.SelectedIndex.Should().Be(0));
        viewModel.MarkdownRendererViewModel.Contents.Should().Be(full);
    }

    [Fact]
    public void Test_ParsingTextWithoutVersionHeadingsFindsNoReleaseNotes()
    {
        var parsed = ParsedChangelog.Parse("Nothing here yet.\nJust prose.");

        parsed.Versions.Should().BeEmpty();
        parsed.GetVersionSection(new Version(1, 0, 0)).Should().BeNull();
    }
}
