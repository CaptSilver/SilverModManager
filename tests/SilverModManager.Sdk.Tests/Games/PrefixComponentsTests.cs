using System.Text.RegularExpressions;
using FluentAssertions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Sdk.Tests.Games;

public partial class PrefixComponentsTests
{
    [Theory]
    [InlineData("vcrun2022")]
    [InlineData("vcrun2019")]
    [InlineData("vcrun2015")]
    [InlineData("VCRUN2022")]
    public void TryResolve_MapsEveryVisualCppAliasToTheSameComponent(string name)
    {
        PrefixComponents.TryResolve(name, out var component).Should().BeTrue();
        component!.Id.Should().Be(PrefixComponents.VisualCppRuntime.Id);
    }

    [Fact]
    public void TryResolve_ResolvesTheDirectXCompilerByItsId()
    {
        PrefixComponents.TryResolve("d3dcompiler_47", out var component).Should().BeTrue();
        component!.Id.Should().Be(PrefixComponents.D3DCompiler47.Id);
    }

    [Fact]
    public void TryResolve_RejectsAnUnknownName()
    {
        PrefixComponents.TryResolve("dotnet48", out _).Should().BeFalse();
    }

    [Fact]
    public void Get_ThrowsForAnIdThatIsNotInTheTable()
    {
        var act = () => PrefixComponents.Get(new PrefixComponentId("dotnet48"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EveryComponent_HasADisplayNameAndAtLeastOneDownload()
    {
        foreach (var component in PrefixComponents.Known)
        {
            component.DisplayName.Should().NotBeNullOrWhiteSpace(component.Id.Value);
            component.Downloads.Should().NotBeEmpty(component.Id.Value);
            component.InstallSteps.Should().NotBeEmpty(component.Id.Value);
        }
    }

    [Fact]
    public void EveryDownload_HasASha256()
    {
        foreach (var download in AllDownloads())
        {
            download.Sha256.Should().MatchRegex("^[0-9a-f]{64}$", download.Url);
            download.FileName.Should().NotBeNullOrWhiteSpace(download.Url);
        }
    }

    [Fact]
    public void EveryDownload_IsHttpsAndNotAMovingReference()
    {
        foreach (var download in AllDownloads())
        {
            download.Url.Should().StartWith("https://");
            download.Url.Should().NotContain("aka.ms");
            download.Url.Should().NotContain("/master/");
            download.Url.Should().NotContain("/main/");
        }
    }

    [Fact]
    public void EveryGitHubDownload_IsPinnedToACommit()
    {
        foreach (var download in AllDownloads().Where(d => d.Url.StartsWith("https://github.com/", StringComparison.Ordinal)))
            CommitSegment().IsMatch(download.Url).Should().BeTrue(download.Url);
    }

    [Fact]
    public void EveryInstallStep_NamesADownloadOfItsComponent()
    {
        foreach (var component in PrefixComponents.Known)
        {
            var files = component.Downloads.Select(d => d.FileName).ToHashSet();
            foreach (var step in component.InstallSteps)
                files.Should().Contain(step.FileName, component.Id.Value);
        }
    }

    [Fact]
    public void VisualCppRuntime_InstallsBothArchitecturesSilentlyWithMshtmlDisabled()
    {
        var component = PrefixComponents.VisualCppRuntime;

        component.InstallSteps.Should().OnlyContain(s => s.Kind == PrefixInstallStepKind.RunInstaller);
        component.InstallSteps.Select(s => s.Architecture).Should().BeEquivalentTo([PrefixArchitecture.X86, PrefixArchitecture.X64]);
        component.InstallSteps.Should().OnlyContain(s => s.Arguments.SequenceEqual(new[] { "/install", "/quiet", "/norestart" }));
        component.Environment.Should().Contain("WINEDLLOVERRIDES", "mshtml=d");
    }

    [Fact]
    public void D3DCompiler47_CopiesTheDllIntoBothSystemDirectories()
    {
        var steps = PrefixComponents.D3DCompiler47.InstallSteps;

        steps.Should().OnlyContain(s => s.Kind == PrefixInstallStepKind.CopyDll);
        steps.Select(s => s.Architecture).Should().BeEquivalentTo([PrefixArchitecture.X86, PrefixArchitecture.X64]);
        steps.Should().OnlyContain(s => s.DestinationName == "d3dcompiler_47.dll");
    }

    [Theory]
    [InlineData("dotnetdesktop8")]
    [InlineData("DOTNETDESKTOP8")]
    public void TryResolve_ResolvesTheDotNet8DesktopRuntimeById(string name)
    {
        PrefixComponents.TryResolve(name, out var component).Should().BeTrue();
        component!.Id.Should().Be(PrefixComponents.DotNetDesktop8.Id);
    }

    [Fact]
    public void DotNetDesktop8_IsKnownButNotInTheSetEveryPrefixGets()
    {
        PrefixComponents.Known.Should().Contain(PrefixComponents.DotNetDesktop8);
        PrefixComponents.All.Should().NotContain(PrefixComponents.DotNetDesktop8);
    }

    [Fact]
    public void DotNetDesktop8_RunsTheVersionedX64InstallerSilently()
    {
        var component = PrefixComponents.DotNetDesktop8;

        var download = component.Downloads.Should().ContainSingle().Subject;
        download.Url.Should().Be("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.33/windowsdesktop-runtime-8.0.33-win-x64.exe");
        download.Sha256.Should().Be("4de14de0b327e801d7a691cb1afc8a00db5313a698ef53b979fc6f84fce631de");
        var step = component.InstallSteps.Should().ContainSingle().Subject;
        step.Kind.Should().Be(PrefixInstallStepKind.RunInstaller);
        step.FileName.Should().Be(download.FileName);
        step.Architecture.Should().Be(PrefixArchitecture.X64);
        step.Arguments.Should().Equal("/install", "/quiet", "/norestart");
    }

    [Fact]
    public void DotNetDesktop8_IsDetectedByItsVersionedFolderBecauseItLeavesNothingInSystem32()
    {
        var component = PrefixComponents.DotNetDesktop8;

        component.NativeFiles.Should().BeEmpty();
        component.InstalledFolders.Should().ContainSingle()
            .Which.Should().Be(new PrefixInstalledFolder("Program Files/dotnet/shared/Microsoft.WindowsDesktop.App", "8."));
    }

    private static IEnumerable<PrefixDownload> AllDownloads() => PrefixComponents.Known.SelectMany(c => c.Downloads);

    [GeneratedRegex("/[0-9a-f]{40}/")]
    private static partial Regex CommitSegment();
}
