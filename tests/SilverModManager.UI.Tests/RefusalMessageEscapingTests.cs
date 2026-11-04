using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.UI.Tests;

/// <summary>
/// A refusal that wraps another failure shows the inner message once: a refusal's message is Markdown already and an
/// ordinary exception's is not.
/// </summary>
public class RefusalMessageEscapingTests
{
    private const string Host = "my_host *x*";

    private static readonly DownloadStalledException Refusal = new(Host);

    [Fact]
    public void Test_BatchInstallException_KeepsARefusalReasonAsItIsAndEscapesAnythingElse()
    {
        new BatchInstallException("Mod", Refusal).Message.Should().EndWith(Refusal.Message);
        new BatchInstallException("Mod", new IOException(Host)).Message.Should().EndWith(MarkdownText.Inline(Host));
    }

    [Fact]
    public void Test_MergeRefusedException_PlanFailed_KeepsARefusalCauseAsItIsAndEscapesAnythingElse()
    {
        MergeRefusedException.PlanFailed(Refusal).Message.Should().EndWith(Refusal.Message);
        MergeRefusedException.PlanFailed(new IOException(Host)).Message.Should().EndWith(MarkdownText.Inline(Host));
    }

    [Fact]
    public void Test_MergeApplyRefusedException_PlanFailed_KeepsARefusalCauseAsItIsAndEscapesAnythingElse()
    {
        MergeApplyRefusedException.PlanFailed(Refusal).Message.Should().EndWith(Refusal.Message);
        MergeApplyRefusedException.PlanFailed(new IOException(Host)).Message.Should().EndWith(MarkdownText.Inline(Host));
    }
}
