using FluentAssertions;
using NexusMods.App.UI.Overlays.Updater;
using NexusMods.Sdk;

namespace NexusMods.UI.Tests;

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

    public static TheoryData<string, InstallationMethod> TestData_Matches()
    {
        return new TheoryData<string, InstallationMethod>
        {
            { "App.linux-x64.zip", InstallationMethod.Archive },
            { "App.AppImage", InstallationMethod.AppImage},
        };
    }
}
