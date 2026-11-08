using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.App.UI.Overlays.Updater;
using SilverModManager.App.UI.Settings;
using SilverModManager.Networking.GitHub;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using NSubstitute;

namespace SilverModManager.UI.Tests;

public class UpdateCheckerTests
{
    private static readonly InstallationMethod[] InstallationMethods = Enum.GetValues<InstallationMethod>();

    [Theory]
    [MemberData(nameof(TestData_Matches))]
    public void AFileOnlyMatchesTheInstallationMethodItIsBuiltFor(string fileName, InstallationMethod installationMethod)
    {
        // NOTE(erri120): tests all permutations and makes sure that the givenFile only matches the provided method
        foreach (var otherMethod in InstallationMethods)
        {
            var expected = otherMethod == installationMethod;
            var actual = UpdateChecker.Matches(fileName, otherMethod, Architecture.X64);
            actual.Should().Be(expected, because: $"fileName={fileName} method={otherMethod}");
        }
    }

    [Theory]
    [InlineData("App.win-x64.zip")]
    [InlineData("App.osx-arm64.zip")]
    public void AFileForAnotherPlatformIsRejected(string fileName)
    {
        foreach (var method in InstallationMethods)
        {
            UpdateChecker.Matches(fileName, method, Architecture.X64).Should().BeFalse(because: $"fileName={fileName} method={method}");
        }
    }

    [Theory]
    [InlineData("SilverModManager.x86_64.AppImage", Architecture.X64, true)]
    [InlineData("SilverModManager.x86_64.AppImage", Architecture.Arm64, false)]
    [InlineData("SilverModManager.aarch64.AppImage", Architecture.Arm64, true)]
    [InlineData("SilverModManager.aarch64.AppImage", Architecture.X64, false)]
    [InlineData("App.AppImage", Architecture.X64, false)]
    [InlineData("App.AppImage", Architecture.Arm64, false)]
    public void AnAppImageAssetIsOnlyOfferedToTheCpuInItsName(string fileName, Architecture architecture, bool expected)
    {
        UpdateChecker.Matches(fileName, InstallationMethod.AppImage, architecture).Should().Be(expected);
    }

    [Theory]
    [InlineData("App.linux-x64.zip", Architecture.X64, true)]
    [InlineData("App.linux-x64.zip", Architecture.Arm64, false)]
    [InlineData("App.linux-arm64.zip", Architecture.Arm64, true)]
    [InlineData("App.linux-arm64.zip", Architecture.X64, false)]
    public void AnArchiveAssetIsOnlyOfferedToTheCpuInItsRuntimeIdentifier(string fileName, Architecture architecture, bool expected)
    {
        UpdateChecker.Matches(fileName, InstallationMethod.Archive, architecture).Should().Be(expected);
    }

    [Fact]
    public void AReleaseWithOnlyTheOtherCpusAppImageYieldsNoAsset()
    {
        var release = MakeRelease("SilverModManager.x86_64.AppImage");

        MakeChecker().TryGetMatchingReleaseAsset(release, InstallationMethod.AppImage, Architecture.Arm64, out var asset).Should().BeFalse();
        asset.Should().BeNull();
    }

    [Fact]
    public void AReleaseWithBothAppImagesGivesEachCpuItsOwn()
    {
        var release = MakeRelease("SilverModManager.x86_64.AppImage", "SilverModManager.aarch64.AppImage");
        var checker = MakeChecker();

        checker.TryGetMatchingReleaseAsset(release, InstallationMethod.AppImage, Architecture.X64, out var x64).Should().BeTrue();
        x64!.Name.Should().Be("SilverModManager.x86_64.AppImage");
        checker.TryGetMatchingReleaseAsset(release, InstallationMethod.AppImage, Architecture.Arm64, out var arm64).Should().BeTrue();
        arm64!.Name.Should().Be("SilverModManager.aarch64.AppImage");
    }

    [Theory]
    [InlineData(InstallationMethod.AppImage)]
    [InlineData(InstallationMethod.Archive)]
    public void AnUnknownCpuIsOfferedNothing(InstallationMethod method)
    {
        var release = MakeRelease("SilverModManager.x86_64.AppImage", "SilverModManager.aarch64.AppImage", "App.linux-x64.zip", "App.linux-arm64.zip");

        MakeChecker().TryGetMatchingReleaseAsset(release, method, Architecture.Wasm, out var asset).Should().BeFalse();
        asset.Should().BeNull();
    }

    private static UpdateChecker MakeChecker()
    {
        var services = new ServiceCollection()
            .AddSingleton(Substitute.For<IGitHubApi>())
            .AddLogging()
            .BuildServiceProvider();
        return new UpdateChecker(services);
    }

    private static Release MakeRelease(params string[] assetNames) => new(
        Url: "https://api.example.com/release", AssetsUrl: "https://api.example.com/assets", HtmlUrl: "https://example.com/release", Id: 1,
        TagName: "v2.0.0", TargetCommitish: "main", Name: "2.0.0", IsDraft: false, IsPrerelease: false,
        CreatedAt: DateTimeOffset.UnixEpoch, PublishedAt: DateTimeOffset.UnixEpoch,
        Assets: assetNames.Select((name, i) => new Asset("https://api.example.com/asset", i + 1, name, "application/octet-stream", "uploaded", 1, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "https://example.com/" + name)).ToArray(),
        TarballUrl: "", ZipballUrl: "", Body: "");

    [Fact]
    public void Test_ReleaseFeedIsOffByDefault()
    {
        new UpdaterSettings().ReleaseFeedEnabled.Should().BeFalse();
    }

    [Fact]
    public void Test_ShouldCheckForUpdate_FeedDisabled()
    {
        var settings = new UpdaterSettings { ReleaseFeedEnabled = false };
        foreach (var method in InstallationMethods)
        {
            UpdateChecker.ShouldCheckForUpdate(new Version(1, 0, 0), method, settings)
                .Should().BeFalse(because: $"method={method}");
        }
    }

    [Theory]
    [InlineData(InstallationMethod.AppImage, true)]
    [InlineData(InstallationMethod.Archive, true)]
    [InlineData(InstallationMethod.Manually, false)]
    public void Test_ShouldCheckForUpdate_FeedEnabled(InstallationMethod method, bool expected)
    {
        var settings = new UpdaterSettings { ReleaseFeedEnabled = true };
        UpdateChecker.ShouldCheckForUpdate(new Version(1, 0, 0), method, settings).Should().Be(expected);
    }

    [Fact]
    public void Test_ShouldCheckForUpdate_ReadsSettingsManager()
    {
        var settingsManager = Substitute.For<ISettingsManager>();
        settingsManager.Get<UpdaterSettings>().Returns(new UpdaterSettings { ReleaseFeedEnabled = false });

        UpdateChecker.ShouldCheckForUpdate(settingsManager).Should().BeFalse();
        settingsManager.Received(1).Get<UpdaterSettings>();
    }

    [Fact]
    public async Task Test_FetchUpdateRelease_AsksTheForkRepository()
    {
        var gitHubApi = Substitute.For<IGitHubApi>();
        gitHubApi
            .FetchLatestRelease(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IComparer<Release>?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Release?>((Release?)null));

        var services = new ServiceCollection()
            .AddSingleton(gitHubApi)
            .AddSingleton(NullLoggerFactory.Instance)
            .AddLogging()
            .BuildServiceProvider();

        var checker = new UpdateChecker(services);
        var release = await checker.FetchUpdateRelease(new Version(1, 0, 0), TestContext.Current.CancellationToken);

        release.Should().BeNull();
        await gitHubApi.Received(1).FetchLatestRelease("CaptSilver", "SilverModManager", Arg.Any<IComparer<Release>?>(), TestContext.Current.CancellationToken);
    }

    public static TheoryData<string, InstallationMethod> TestData_Matches()
    {
        return new TheoryData<string, InstallationMethod>
        {
            { "App.linux-x64.zip", InstallationMethod.Archive },
            { "App.x86_64.AppImage", InstallationMethod.AppImage },
        };
    }
}
