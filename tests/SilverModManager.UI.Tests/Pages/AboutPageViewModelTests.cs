using FluentAssertions;
using SilverModManager.App.UI.Pages.About;
using SilverModManager.Sdk;

namespace SilverModManager.UI.Tests.Pages;

public class AboutPageViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task Test_ActivatingShowsProductVersionAndLicense()
    {
        // Activation binds on the UI thread, so it has to start there.
        using var wrapper = await OnUi(() => Task.FromResult(GetActivatedViewModel<IAboutPageViewModel>()));

        await Eventually(() =>
        {
            var contents = wrapper.VM.MarkdownRendererViewModel.Contents;
            contents.Should().Contain(ApplicationConstants.ProductName);
            contents.Should().Contain(ApplicationConstants.Version.ToString());
            contents.Should().Contain("GPL");
            contents.Should().Contain("not affiliated");
            contents.Should().Contain("github.com/Nexus-Mods/NexusMods.App");
        });
    }

    [Fact]
    public async Task Test_ContentsCarryTheFullNoticeText()
    {
        // Read independently of the view model so a missing or renamed resource fails here.
        await using var stream = typeof(AboutPageViewModel).Assembly.GetManifestResourceStream("SilverModManager.App.UI.NOTICE")
                                 ?? throw new InvalidOperationException("NOTICE is not embedded in the UI assembly");
        using var reader = new StreamReader(stream);
        var notice = (await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).Trim();

        using var wrapper = await OnUi(() => Task.FromResult(GetActivatedViewModel<IAboutPageViewModel>()));

        await Eventually(() => wrapper.VM.MarkdownRendererViewModel.Contents.Should().Contain(notice));
    }

    [Fact]
    public void Test_BuildContentsOmitsTheBuildShaWhenUnknown()
    {
        var contents = AboutPageViewModel.BuildContents("v: {{Version}}{{Build}}|{{Notice}}|{{ThirdPartyNoticesUrl}}", "NOTICE TEXT", new Version(1, 2, 3), commitHash: null, new Uri("https://example.test/repo"));

        contents.Should().Be("v: 1.2.3|NOTICE TEXT|https://example.test/repo/blob/main/THIRD-PARTY-NOTICES.md");
    }

    [Fact]
    public void Test_BuildContentsIncludesTheBuildShaWhenKnown()
    {
        var contents = AboutPageViewModel.BuildContents("v: {{Version}}{{Build}}", "", new Version(1, 2, 3), commitHash: "abc1234", new Uri("https://example.test/repo"));

        contents.Should().Be("v: 1.2.3 (build abc1234)");
    }
}
