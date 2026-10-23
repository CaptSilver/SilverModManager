using System.Text;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.App.UI.Controls.MarkdownRenderer;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Sdk;
using NexusMods.Sdk.IO;
using NexusMods.UI.Sdk.Icons;

namespace NexusMods.App.UI.Pages.About;

[UsedImplicitly]
public class AboutPageViewModel : APageViewModel<IAboutPageViewModel>, IAboutPageViewModel
{
    // Both texts ship inside the app so the copyright and modification notice travel with the binary,
    // not only with the repository.
    private const string TemplateResourceName = "NexusMods.App.UI.About.md";
    private const string NoticeResourceName = "NexusMods.App.UI.NOTICE";

    public IMarkdownRendererViewModel MarkdownRendererViewModel { get; }

    public AboutPageViewModel(
        IServiceProvider serviceProvider,
        IWindowManager windowManager) : base(windowManager)
    {
        TabIcon = IconValues.Info;
        TabTitle = Language.AboutPageViewModel_TabTitle;

        MarkdownRendererViewModel = serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();

        MarkdownRendererViewModel.Contents = BuildContents(
            ReadResource(TemplateResourceName),
            ReadResource(NoticeResourceName),
            ApplicationConstants.Version,
            ApplicationConstants.CommitHash,
            ConstantLinks.GitHubUri
        );
    }

    /// <summary>
    /// Fills the About template. The build sha is only known for builds that carry it, so the
    /// <c>{{Build}}</c> slot is empty rather than showing a placeholder when it is missing.
    /// </summary>
    public static string BuildContents(string template, string notice, Version version, string? commitHash, Uri repositoryUri)
    {
        var build = string.IsNullOrWhiteSpace(commitHash) ? string.Empty : $" (build {commitHash})";
        var thirdPartyNoticesUrl = $"{repositoryUri.AbsoluteUri.TrimEnd('/')}/blob/main/THIRD-PARTY-NOTICES.md";

        return template
            .Replace("{{ProductName}}", ApplicationConstants.ProductName)
            .Replace("{{Version}}", version.ToString())
            .Replace("{{Build}}", build)
            .Replace("{{ThirdPartyNoticesUrl}}", thirdPartyNoticesUrl)
            .Replace("{{Notice}}", notice.Trim());
    }

    private static string ReadResource(string name)
    {
        // The embedded-resource factory completes synchronously, so blocking on it in the constructor is safe.
        using var stream = new EmbeddedResourceStreamFactory<AboutPageViewModel>(name).GetStreamAsync().GetAwaiter().GetResult();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
