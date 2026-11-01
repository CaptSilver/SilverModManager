using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts.Synchronizers;

namespace SilverModManager.Library.Tests;

internal static class LibraryServiceRegistration
{
    /// <summary>
    /// The real <see cref="LibraryService"/> over the test database. It reaches the loadout manager and the garbage
    /// collector only when a loadout is touched, which these tests never do, so those two are stand-ins.
    /// </summary>
    public static IServiceCollection AddLibraryServiceForTests(this IServiceCollection services)
        => services
            .AddSingleton(Substitute.For<ILoadoutManager>())
            .AddSingleton(Substitute.For<IGarbageCollectorRunner>())
            .AddSingleton<ILibraryService, LibraryService>();
}
