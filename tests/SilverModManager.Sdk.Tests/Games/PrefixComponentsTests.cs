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
        foreach (var component in PrefixComponents.All)
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
        foreach (var component in PrefixComponents.All)
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

    private static IEnumerable<PrefixDownload> AllDownloads() => PrefixComponents.All.SelectMany(c => c.Downloads);

    [GeneratedRegex("/[0-9a-f]{40}/")]
    private static partial Regex CommitSegment();
}
