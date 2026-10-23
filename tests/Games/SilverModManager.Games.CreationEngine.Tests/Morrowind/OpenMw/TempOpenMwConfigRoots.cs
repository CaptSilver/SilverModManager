using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.CreationEngine.Morrowind.OpenMw;
using NexusMods.Paths;

namespace SilverModManager.Games.CreationEngine.Tests.Morrowind.OpenMw;

/// <summary>
/// Two empty OpenMW config roots in temp folders. Every service graph that registers the Morrowind game must
/// add this after AddCreationEngine (the last registration wins): the production roots resolve to the
/// developer's real config folders, and the test host does not redirect $HOME, so the Flatpak one is real.
/// </summary>
public sealed class TempOpenMwConfigRoots(TemporaryFileManager temp) : IOpenMwConfigRoots
{
    public IReadOnlyList<AbsolutePath> Candidates { get; } =
    [
        temp.CreateFolder("openmw-native").Path,
        temp.CreateFolder("openmw-flatpak").Path,
    ];
}

public static class TempOpenMwConfigRootsExtensions
{
    public static IServiceCollection AddTempOpenMwConfigRoots(this IServiceCollection services) =>
        services.AddSingleton<IOpenMwConfigRoots>(s => new TempOpenMwConfigRoots(s.GetRequiredService<TemporaryFileManager>()));
}
