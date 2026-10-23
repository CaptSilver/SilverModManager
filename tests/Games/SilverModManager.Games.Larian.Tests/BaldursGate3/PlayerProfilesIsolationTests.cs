using System.Reflection;
using FluentAssertions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Larian.BaldursGate3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3;

/// <summary>
/// Every BG3 fixture runs in parallel with the others, so each one needs its own PlayerProfiles folder. A shared
/// one means two classes read and write the same modsettings.lsx.
/// </summary>
public class PlayerProfilesIsolationTests(ITestOutputHelper helper)
{
    [Fact]
    public async Task EachFixtureGetsItsOwnModsettingsPath()
    {
        // Two instances of the same class are included: parallel classes are not the only way to collide.
        IAsyncLifetime[] fixtures =
        [
            new BG3InstallerTests(helper),
            new BG3InstallerTests(helper),
            new BG3WineDllOverrideTests(helper),
            new BG3WineDllOverrideDiagnosticTests(helper),
            new BG3WineDllOverrideSteamDiagnosticTests(helper),
            new BG3DependencyDiagnosticTests(helper),
            new BG3DependencyFallbackDiagnosticTests(helper),
        ];

        try
        {
            foreach (var fixture in fixtures) await fixture.InitializeAsync();

            var paths = fixtures.Select(ModsettingsOnDisk).ToArray();
            paths.Should().OnlyHaveUniqueItems();
        }
        finally
        {
            foreach (var fixture in fixtures) await fixture.DisposeAsync();
        }
    }

    private static AbsolutePath ModsettingsOnDisk(object fixture)
    {
        // The installation is a protected member of the shared fixture base class.
        var field = fixture.GetType().GetField("GameInstallation", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"{fixture.GetType()} has no GameInstallation");
        var installation = (GameInstallation)field.GetValue(fixture)!;
        return installation.Locations.ToAbsolutePath(new GamePath(Bg3Constants.PlayerProfilesLocationId, "modsettings.lsx"));
    }
}
