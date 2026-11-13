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
    public void ForNativeFile_FindsADetectOnlyComponent()
    {
        var component = PrefixComponents.ForNativeFile("msvbvm60.dll");
        component!.Id.Value.Should().Be("vb6run");
        component.Installable.Should().BeFalse();
    }

    [Fact]
    public void ForNativeFile_IsCaseInsensitive()
        => PrefixComponents.ForNativeFile("MSVBVM60.DLL")!.Id.Value.Should().Be("vb6run");

    [Fact]
    public void ForNativeFile_FindsAnInstallableComponent()
    {
        var component = PrefixComponents.ForNativeFile("vcruntime140.dll");
        component.Should().BeSameAs(PrefixComponents.VisualCppRuntime);
        component!.Installable.Should().BeTrue();
    }

    [Fact]
    public void ForNativeFile_ReturnsNullForAnUnknownDll()
        => PrefixComponents.ForNativeFile("xinput1_3.dll").Should().BeNull();

    [Fact]
    public void DetectOnlyComponents_AreKnownButNotInAll()
    {
        PrefixComponents.Known.Select(c => c.Id.Value).Should().Contain("vb6run");
        PrefixComponents.All.Select(c => c.Id.Value).Should().NotContain("vb6run");
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
    public void EveryAppInstallableComponent_IsInstallable()
    {
        // Pins the installable set so an entry that loses its steps cannot slip into the detect-only group.
        foreach (var component in PrefixComponents.All.Append(PrefixComponents.DotNetDesktop8))
            component.Installable.Should().BeTrue(component.Id.Value);
    }

    [Fact]
    public void EveryComponent_HasADisplayNameAndAtLeastOneDownload()
    {
        foreach (var component in PrefixComponents.Known.Where(c => c.Installable))
        {
            component.DisplayName.Should().NotBeNullOrWhiteSpace(component.Id.Value);
            component.Downloads.Should().NotBeEmpty(component.Id.Value);
            component.InstallSteps.Should().NotBeEmpty(component.Id.Value);
        }
    }

    [Fact]
    public void EveryDetectOnlyComponent_HasANameNativeFilesAndNothingToInstall()
    {
        foreach (var component in PrefixComponents.Known.Where(c => !c.Installable))
        {
            component.DisplayName.Should().NotBeNullOrWhiteSpace(component.Id.Value);
            component.NativeFiles.Should().NotBeEmpty(component.Id.Value);
            component.Downloads.Should().BeEmpty(component.Id.Value);
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
        download.Url.Should().Be("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.34/windowsdesktop-runtime-8.0.34-win-x64.exe");
        download.Sha256.Should().Be("37aa8a02acc1973ddd7e3de993a02007ccef26b48fa35b09d087fa8ab4602795");
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

    [Fact]
    public void DotNetDesktop6_IsResolvableByIdAndKnownButNotInTheSetEveryPrefixGets()
    {
        PrefixComponents.TryResolve("dotnetdesktop6", out var component).Should().BeTrue();
        component!.Installable.Should().BeTrue();
        PrefixComponents.Known.Should().Contain(component);
        PrefixComponents.All.Should().NotContain(component);
        PrefixComponents.Known.Select(c => c.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void DotNetDesktop6_RunsTheVersionedX64InstallerSilently()
    {
        var component = PrefixComponents.DotNetDesktop6;

        var download = component.Downloads.Should().ContainSingle().Subject;
        download.Url.Should().StartWith("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/6.0.").And.EndWith("-win-x64.exe");
        download.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        var step = component.InstallSteps.Should().ContainSingle().Subject;
        step.Kind.Should().Be(PrefixInstallStepKind.RunInstaller);
        step.FileName.Should().Be(download.FileName);
        step.Architecture.Should().Be(PrefixArchitecture.X64);
        step.Arguments.Should().Equal("/install", "/quiet", "/norestart");
    }

    [Fact]
    public void DotNetDesktop6_IsDetectedByItsVersionedFolderBecauseItLeavesNothingInSystem32()
    {
        var component = PrefixComponents.DotNetDesktop6;

        component.NativeFiles.Should().BeEmpty();
        component.InstalledFolders.Should().ContainSingle()
            .Which.Should().Be(new PrefixInstalledFolder("Program Files/dotnet/shared/Microsoft.WindowsDesktop.App", "6."));
    }

    private static IEnumerable<PrefixDownload> AllDownloads() => PrefixComponents.Known.SelectMany(c => c.Downloads);

    [GeneratedRegex("/[0-9a-f]{40}/")]
    private static partial Regex CommitSegment();
}
