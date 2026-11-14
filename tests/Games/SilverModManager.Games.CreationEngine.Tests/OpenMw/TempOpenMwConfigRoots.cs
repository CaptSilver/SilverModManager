using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.CreationEngine.OpenMw;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

/// <summary>
/// A native and a Flatpak OpenMW root in temp folders, both with a program that exits at once. Every service graph
/// that registers the OpenMW or Morrowind games must add this after AddCreationEngine (the last registration wins):
/// the production roots resolve to the developer's real config folders, and the test host does not redirect $HOME,
/// so the Flatpak one is real.
/// </summary>
public sealed class TempOpenMwConfigRoots : IOpenMwConfigRoots
{
    public TempOpenMwConfigRoots(TemporaryFileManager temp)
    {
        var bin = temp.CreateFolder("openmw-bin").Path;
        NativeProgram = FakeProgram(bin / "openmw");
        FlatpakProgram = FakeProgram(bin / "flatpak");

        Roots =
        [
            new OpenMwConfigRoot(temp.CreateFolder("openmw-native").Path, temp.CreateFolder("openmw-native-data").Path, GameStore.OpenMW, () => NativeProgram, () => NativeGlobalConfig),
            new OpenMwConfigRoot(temp.CreateFolder("openmw-flatpak").Path, temp.CreateFolder("openmw-flatpak-data").Path, GameStore.OpenMWFlatpak, () => FlatpakProgram),
        ];
    }

    public IReadOnlyList<OpenMwConfigRoot> Roots { get; }

    /// <summary>The native program, or null for an install that is gone.</summary>
    public AbsolutePath? NativeProgram { get; set; }

    public AbsolutePath? FlatpakProgram { get; set; }

    /// <summary>The native install's global openmw.cfg, none until a test sets one.</summary>
    public AbsolutePath? NativeGlobalConfig { get; set; }

    public AbsolutePath NativeConfig => Roots[0].ConfigFolder;

    public AbsolutePath FlatpakConfig => Roots[1].ConfigFolder;

    public void WriteConfig(int root, string text) => File.WriteAllText((Roots[root].ConfigFolder / "openmw.cfg").GetFullPath(), text);

    private static AbsolutePath FakeProgram(AbsolutePath path)
    {
        File.WriteAllText(path.GetFullPath(), "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(path.GetFullPath(), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}

public static class TempOpenMwConfigRootsExtensions
{
    public static IServiceCollection AddTempOpenMwConfigRoots(this IServiceCollection services) =>
        services.AddSingleton<IOpenMwConfigRoots>(s => new TempOpenMwConfigRoots(s.GetRequiredService<TemporaryFileManager>()));
}
