using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// The GPL asks a modified version to say so prominently, and the fork has no per-file headers
/// to carry that, so the notice files at the repo root are what does it. The same tests keep
/// upstream's release workflow and docs-site tooling from coming back.
/// </summary>
public class NoticeFilesTests
{
    private static string Root => RepoScanGuard.FindRepoRoot();

    private static string ReadRootFile(string name)
    {
        var path = Path.Combine(Root, name);
        File.Exists(path).Should().BeTrue($"{name} must exist at the repo root");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Test_NoticeNamesTheUpstreamProjectAndLicense()
    {
        var notice = ReadRootFile("NOTICE");

        notice.Should().Contain("GPL-3.0-only");
        notice.Should().Contain("https://github.com/Nexus-Mods/NexusMods.App");
        notice.Should().Contain("48284f38c", "the notice names the commit the fork started from");
        notice.Should().Contain("not affiliated");
    }

    [Fact]
    public void Test_NoticeKeepsTheUpstreamAttributionVerbatim()
    {
        var notice = ReadRootFile("NOTICE").ReplaceLineEndings("\n");

        notice.Should().Contain("SilverModManager is a modified version of the Nexus Mods App\n(https://github.com/Nexus-Mods/NexusMods.App), Copyright (C) Nexus Mods,\n");
    }

    [Fact]
    public void Test_NoticeSaysWhichNexusModsNamespacesRemain()
    {
        var notice = ReadRootFile("NOTICE");

        notice.Should().NotContain("NexusMods.* project and namespace names", "first-party names carry the fork's name now");
        foreach (var package in new[] { "NexusMods.Paths", "NexusMods.Hashing", "NexusMods.Archives", "NexusMods.HyperDuck" })
            notice.Should().Contain(package, "the notice names the third-party namespaces that keep upstream's prefix");
        notice.Should().NotContain("MkDocsMaterial", "the docs theme submodule is gone");
    }

    [Fact]
    public void Test_NoticeSaysThePictogramsAreMaterialDesignIcons()
    {
        var notice = ReadRootFile("NOTICE").ReplaceLineEndings("\n").Replace("\n  ", " ");

        notice.Should().Contain("The large pictograms in dialogs and page headers are Material Design Icons");
        notice.Should().NotContain("placeholder images, pictograms", "the pictograms are not Nexus artwork any more");
    }

    [Fact]
    public void Test_NoticeNamesTheVendoredMnemonicDb()
    {
        var notice = ReadRootFile("NOTICE");

        notice.Should().Contain("SilverModManager.MnemonicDB");
        notice.Should().Contain("https://github.com/Nexus-Mods/NexusMods.MnemonicDB");
        notice.Should().Contain("v0.28.2");
        File.Exists(Path.Combine(Root, "src", "SilverModManager.MnemonicDB", "LICENSE")).Should().BeTrue("the vendored source carries its upstream license");
    }

    [Fact]
    public void Test_ThirdPartyNoticesCoverTheBundledFontsAndIcons()
    {
        var notices = ReadRootFile("THIRD-PARTY-NOTICES.md");

        notices.Should().Contain("Inter");
        notices.Should().Contain("SIL Open Font License");
        notices.Should().Contain("Roboto Mono");
        notices.Should().Contain("Apache License");
        notices.Should().Contain("Material Design Icons");
    }

    [Theory]
    [InlineData(".github/workflows/release.yaml", "publishes under upstream's identity to upstream's releases")]
    [InlineData("mkdocs.yml", "configures upstream's documentation site")]
    [InlineData("docs/Nexus", "upstream's docs theme submodule")]
    [InlineData("docs/users", "upstream's user guides and screenshots")]
    [InlineData("docs/images/nexus-logo-white.svg", "the Nexus logo")]
    [InlineData("tests/SilverModManager.Backend.Tests/EventTrackerTests.Test_PrepareRequest.verified.txt", "snapshot of a test that no longer exists")]
    public void Test_RemovedPathsStayRemoved(string relativePath, string reason)
    {
        var path = Path.Combine(Root, relativePath);
        (File.Exists(path) || Directory.Exists(path)).Should().BeFalse(reason);
    }

    [Fact]
    public void Test_OnlySmapiRemainsAsSubmodule()
    {
        var gitmodules = ReadRootFile(".gitmodules");

        gitmodules.Should().NotContain("docs/Nexus");
        gitmodules.Should().Contain("extern/SMAPI");
    }
}
