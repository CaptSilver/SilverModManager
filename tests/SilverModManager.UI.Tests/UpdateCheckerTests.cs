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
    public void Test_Matches(string fileName, InstallationMethod installationMethod)
    {
        // NOTE(erri120): tests all permutations and makes sure that the givenFile only matches the provided method
        foreach (var otherMethod in InstallationMethods)
        {
            var expected = otherMethod == installationMethod;
            var actual = UpdateChecker.Matches(fileName, otherMethod);
            actual.Should().Be(expected, because: $"fileName={fileName} method={otherMethod}");
        }
    }

    [Theory]
    [InlineData("App.win-x64.zip")]
    [InlineData("App.osx-arm64.zip")]
    public void Test_Matches_RejectsOtherPlatforms(string fileName)
    {
        foreach (var method in InstallationMethods)
        {
            UpdateChecker.Matches(fileName, method).Should().BeFalse(because: $"fileName={fileName} method={method}");
        }
    }

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
            { "App.AppImage", InstallationMethod.AppImage},
        };
    }
}
