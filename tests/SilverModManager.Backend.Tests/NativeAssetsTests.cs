using FluentAssertions;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Backend.Tests;

/// <summary>
/// NuGet packages carry native libraries for every platform. Only Linux is a supported host,
/// so the build output must hold the Linux ones and nothing for Windows or macOS
/// (see PruneNonLinuxRuntimeAssets in Directory.Build.targets).
/// </summary>
public class NativeAssetsTests
{
    private static string RuntimesDirectory => Path.Combine(AppContext.BaseDirectory, "runtimes");

    [Fact]
    public void Test_OutputHasNoNonLinuxRuntimeAssets()
    {
        var foreign = Directory.EnumerateDirectories(RuntimesDirectory)
            .Select(Path.GetFileName)
            .Where(name => name != "unix") // managed assets shared by Linux and macOS
            .Where(name => !name!.StartsWith("linux", StringComparison.Ordinal) || name.StartsWith("linux-bionic", StringComparison.Ordinal))
            .ToArray();

        foreign.Should().BeEmpty("only Linux runtime assets belong in the output");
    }

    [Fact]
    public void TheOutputKeepsTheNativesOfTheRunningCpuOnly()
    {
        var rid = BundledNativeLibrary.RuntimeIdentifiers()[^1]; // the portable one: linux-x64 here, linux-arm64 on arm64
        var folders = Directory.EnumerateDirectories(RuntimesDirectory)
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith("linux", StringComparison.Ordinal))
            .ToArray();
        folders.Should().Equal(rid);

        File.Exists(Path.Combine(RuntimesDirectory, rid, "native", "7zz")).Should().BeTrue("the bundled 7-Zip is always under runtimes/<rid>/native");

        // A build with no RuntimeIdentifier keeps NuGet natives under runtimes/<rid>/native; a RID-specific build
        // (publish, or -r linux-arm64 for the emulated runs) flattens them beside the assemblies. Either is right.
        string[] nuget = ["libduckdb.so", "librocksdb.so", "libSkiaSharp.so"];
        foreach (var name in nuget)
        {
            (File.Exists(Path.Combine(RuntimesDirectory, rid, "native", name)) || File.Exists(Path.Combine(AppContext.BaseDirectory, name)))
                .Should().BeTrue($"{name} must be in the output for {rid}");
        }
    }
}
