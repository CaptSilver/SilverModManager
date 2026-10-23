using FluentAssertions;

namespace NexusMods.Backend.Tests;

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
    public void Test_OutputKeepsLinuxX64Natives()
    {
        var natives = Directory.EnumerateFiles(Path.Combine(RuntimesDirectory, "linux-x64", "native"))
            .Select(Path.GetFileName)
            .ToArray();

        natives.Should().Contain("libduckdb.so").And.Contain("librocksdb.so").And.Contain("libSkiaSharp.so");
    }
}
