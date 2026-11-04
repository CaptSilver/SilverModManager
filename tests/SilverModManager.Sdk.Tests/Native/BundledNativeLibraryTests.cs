using System.Runtime.InteropServices;
using FluentAssertions;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Sdk.Tests.Native;

public class BundledNativeLibraryTests
{
    private static readonly string[] LinuxX64 = ["linux-x64"];

    [Fact]
    public void ASingleFileApp_FindsTheLibraryUnderTheAppsBaseDirectory()
    {
        // A single-file publish reports an empty Assembly.Location; the library sits beside the executable.
        var candidates = BundledNativeLibrary.CandidatePaths("libooz.so", assemblyLocation: "", baseDirectory: "/tmp/.mount_app/usr/bin/", LinuxX64);

        candidates.Should().Equal(Path.Combine("/tmp/.mount_app/usr/bin/", "runtimes/linux-x64/native/libooz.so"));
    }

    [Fact]
    public void AnAssemblyOnDisk_IsTriedBeforeTheBaseDirectory()
    {
        var candidates = BundledNativeLibrary.CandidatePaths("libooz.so", assemblyLocation: "/opt/smm/plugins/SilverModManager.Games.FromSoft.dll", baseDirectory: "/opt/smm/", LinuxX64);

        candidates.Should().Equal(
            Path.Combine("/opt/smm/plugins", "runtimes/linux-x64/native/libooz.so"),
            Path.Combine("/opt/smm/", "runtimes/linux-x64/native/libooz.so"));
    }

    [Fact]
    public void TheSameFolderIsOnlyTriedOnce()
    {
        var candidates = BundledNativeLibrary.CandidatePaths("libooz.so", assemblyLocation: "/opt/smm/SilverModManager.Games.FromSoft.dll", baseDirectory: "/opt/smm/", LinuxX64);

        candidates.Should().Equal(Path.Combine("/opt/smm", "runtimes/linux-x64/native/libooz.so"));
    }

    [Fact]
    public void ADistroSpecificRid_IsTriedBeforeThePortableOneInEachFolder()
    {
        var candidates = BundledNativeLibrary.CandidatePaths("libsmmtexenc.so", "/opt/smm/A.dll", "/app/", ["fedora.44-arm64", "linux-arm64"]);

        candidates.Should().Equal(
            "/opt/smm/runtimes/fedora.44-arm64/native/libsmmtexenc.so",
            "/opt/smm/runtimes/linux-arm64/native/libsmmtexenc.so",
            "/app/runtimes/fedora.44-arm64/native/libsmmtexenc.so",
            "/app/runtimes/linux-arm64/native/libsmmtexenc.so");
    }

    [Theory]
    [InlineData("linux-x64", Architecture.X64, new[] { "linux-x64" })]
    [InlineData("ubuntu.22.04-x64", Architecture.X64, new[] { "ubuntu.22.04-x64", "linux-x64" })]
    [InlineData("linux-arm64", Architecture.Arm64, new[] { "linux-arm64" })]
    [InlineData("", Architecture.Arm64, new[] { "linux-arm64" })]
    public void RuntimeIdentifiers_AreTheRuntimesOwnThenThePortableLinuxOne(string runtimeIdentifier, Architecture architecture, string[] expected)
        => BundledNativeLibrary.RuntimeIdentifiers(runtimeIdentifier, architecture).Should().Equal(expected);

    [Fact]
    public void ALibraryThatIsNowhere_IsNotLoadedAndEveryPlaceTriedIsReported()
    {
        var loaded = BundledNativeLibrary.TryLoad("libsmm-no-such-library.so", typeof(BundledNativeLibraryTests).Assembly, null, out var handle, out var tried);

        loaded.Should().BeFalse();
        handle.Should().Be(IntPtr.Zero);
        tried.Should().Contain(path => path.EndsWith("runtimes/" + BundledNativeLibrary.RuntimeIdentifiers()[0] + "/native/libsmm-no-such-library.so"));
        tried.Should().Contain("libsmm-no-such-library.so");
    }

    [Fact]
    public void TheNotFoundMessageNamesEveryPathTriedAndTheScriptThatBuildsTheLibrary()
    {
        var message = BundledNativeLibrary.NotFoundMessage("libooz.so", "scripts/build-ooz.sh", ["/a/runtimes/linux-x64/native/libooz.so", "libooz.so"]);

        message.Should().Contain("libooz.so").And.Contain("/a/runtimes/linux-x64/native/libooz.so").And.Contain("scripts/build-ooz.sh");
    }
}
