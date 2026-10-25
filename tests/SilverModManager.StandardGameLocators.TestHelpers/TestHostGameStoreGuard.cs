using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.StandardGameLocators.TestHelpers;

/// <summary>
/// Checks that a test host cannot see the developer's real game stores. The app's store locators read
/// Steam, Heroic, Lutris and Wine prefixes from the home folder, so a host that registers them finds
/// whatever games the machine has and attaches test loadouts to them.
/// </summary>
public static class TestHostGameStoreGuard
{
    private const string UserInstallLocatorName = "SilverModManager.Backend.Games.Locators.UserInstallLocator";

    /// <summary>
    /// Fails when the host registers a locator other than the stubbed ones, or when a located
    /// installation is outside the folders tests create their files in.
    /// </summary>
    public static void AssertIsolated(IServiceProvider serviceProvider)
    {
        AssertOnlyStubbedLocators(serviceProvider);
        AssertInstallationsAreInTestFolders(serviceProvider);
    }

    /// <summary>
    /// Fails when any registered <see cref="IGameLocator"/> is one of the app's store locators
    /// (Steam, Heroic, Lutris, GOG/Epic through Wine) instead of a stub or the user-install locator.
    /// </summary>
    public static void AssertOnlyStubbedLocators(IServiceProvider serviceProvider)
    {
        var real = serviceProvider.GetServices<IGameLocator>()
            .Select(locator => locator.GetType())
            .Where(type => !IsStubbed(type))
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        real.Should().BeEmpty("test hosts must not register locators that read the machine's game stores");
    }

    /// <summary>
    /// Fails when the registry yields an installation whose folder is not under the temp folder, the
    /// test binaries' folder or a test scratch folder.
    /// </summary>
    public static void AssertInstallationsAreInTestFolders(IServiceProvider serviceProvider)
    {
        var fileSystem = FileSystem.Shared;
        var roots = new[]
        {
            fileSystem.FromUnsanitizedFullPath(Path.GetTempPath()),
            fileSystem.GetKnownPath(KnownPath.EntryDirectory),
            fileSystem.FromUnsanitizedFullPath(TestScratch.HostTemp),
            fileSystem.FromUnsanitizedFullPath(TestScratch.Root),
            fileSystem.FromUnsanitizedFullPath(TestScratch.ReflinkRoot),
        };

        var outside = serviceProvider.GetRequiredService<IGameRegistry>()
            .LocateGameInstallations()
            .Select(installation => installation.LocatorResult.Path)
            .Where(path => !roots.Any(root => path.InFolder(root)))
            .Select(path => path.GetFullPath())
            .ToArray();

        outside.Should().BeEmpty("test hosts must not locate games outside the test folders");
    }

    /// <summary>
    /// Fails when the host's <see cref="IGameArtSource"/> is not the placeholder one. The app's source reads
    /// cover art from the real Steam folder's <c>appcache/librarycache</c>.
    /// </summary>
    public static void AssertGameArtIsNotReadFromSteam(IServiceProvider serviceProvider)
    {
        serviceProvider.GetRequiredService<IGameArtSource>().Should().BeOfType<PlaceholderGameArtSource>(
            "test hosts must not read cover art from the machine's Steam cache");
    }

    private static bool IsStubbed(Type type)
        => type.FullName == UserInstallLocatorName
           || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(UniversalStubbedGameLocator<>));
}
