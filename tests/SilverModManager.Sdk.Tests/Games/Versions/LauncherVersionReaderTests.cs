using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games.Versions;

namespace SilverModManager.Sdk.Tests.Games.Versions;

public class LauncherVersionReaderTests : IDisposable
{
    private readonly LauncherFixture _fixture = new();
    private readonly ITestOutputHelper _output;
    private readonly LauncherVersionReader _reader = new();

    public LauncherVersionReaderTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _fixture.Dispose();

    // What the PE-only path sees on Linux for a Roslyn-built assembly: the managed attributes answer whether or not
    // the file has a Win32 VERSIONINFO resource, and an informational version that isn't one reads as 0.0.0.0.
    [Theory]
    [InlineData("3.4.5", "2.3.4.0", true, "3.4.5", "2.3.4.0")]
    [InlineData("3.4.5", "2.3.4.0", false, "3.4.5", "2.3.4.0")]
    [InlineData("1.6.15-alpha.1+abc123", "1.0.0.0", false, "1.6.15.0", "1.0.0.0")]
    [InlineData("not a version", "2.3.4.0", false, "0.0.0.0", "2.3.4.0")]
    [InlineData("3", "2.3.4.0", false, "3.0.0.0", "2.3.4.0")]
    public void PeFileVersionInfo_Characterisation(string informational, string assembly, bool withResource, string productVersion, string fileVersion)
    {
        _fixture.WriteAssembly(informational, assembly, withResource);

        var fvi = _fixture.Dll.FileInfo.GetFileVersionInfo();
        _output.WriteLine($"info={informational} asm={assembly} resource={withResource}: ProductVersion={fvi.ProductVersion?.ToString() ?? "null"} FileVersion={fvi.FileVersion?.ToString() ?? "null"}");

        fvi.ProductVersion.Should().Be(Version.Parse(productVersion));
        fvi.FileVersion.Should().Be(Version.Parse(fileVersion));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PeReader_Wins_OverDepsJson_ForTheLauncherItself(bool withVersionResource)
    {
        // On Linux the PE reader also answers from a managed DLL's attributes, so both shapes land here.
        _fixture.WriteAssembly("3.4.5", "2.3.4.0", withVersionResource);
        _fixture.WriteDepsJson("1.6.15");

        var result = _reader.Read(_fixture.Dll);

        result.Source.Should().Be(LauncherVersionSource.PeResource);
        result.Version.Value.Should().Be(new Version(3, 4, 5));
    }

    [Fact]
    public void AssemblyMetadata_Used_WhenLauncherHasNoVersionData()
    {
        WriteStubExecutable();
        _fixture.WriteAssembly("3.4.5", "2.3.4.0", withVersionResource: false);

        var result = _reader.Read(_fixture.Exe);

        result.Source.Should().Be(LauncherVersionSource.AssemblyMetadata);
        result.Version.Value.Should().Be(new Version(3, 4, 5));
    }

    [Fact]
    public void AssemblyMetadata_StripsPrereleaseAndBuildSuffix()
    {
        WriteStubExecutable();
        _fixture.WriteAssembly("1.6.15-alpha.1+abc123", "1.0.0.0", withVersionResource: false);

        var result = _reader.Read(_fixture.Exe);

        result.Version.Value.Should().Be(new Version(1, 6, 15));
    }

    [Fact]
    public void AssemblyVersion_UsedWhenInformationalVersionIsNotAVersion()
    {
        WriteStubExecutable();
        _fixture.WriteAssembly("not a version", "2.3.4.0", withVersionResource: false);

        var result = _reader.Read(_fixture.Exe);

        result.Source.Should().Be(LauncherVersionSource.AssemblyMetadata);
        result.Version.Value.Should().Be(new Version(2, 3, 4, 0));
    }

    [Fact]
    public void AssemblyMetadata_Wins_OverDepsJson()
    {
        WriteStubExecutable();
        _fixture.WriteAssembly("3.4.5", "2.3.4.0", withVersionResource: false);
        _fixture.WriteDepsJson("1.6.15");

        var result = _reader.Read(_fixture.Exe);

        result.Source.Should().Be(LauncherVersionSource.AssemblyMetadata);
        result.Version.Value.Should().Be(new Version(3, 4, 5));
    }

    [Fact]
    public void AssemblyMetadata_WithoutAnyVersion_FallsToDepsJson()
    {
        WriteStubExecutable();
        _fixture.WriteAssembly("not a version", "0.0.0.0", withVersionResource: false);
        _fixture.WriteDepsJson("1.6.15");

        var result = _reader.Read(_fixture.Exe);

        result.Source.Should().Be(LauncherVersionSource.DepsJson);
        result.Version.Value.Should().Be(new Version(1, 6, 15));
    }

    [Fact]
    public void DepsJson_Used_WhenLauncherAndDllHaveNoVersionData()
    {
        WriteStubExecutable();
        _fixture.WriteDepsJson("1.6.15");

        var result = _reader.Read(_fixture.Exe);

        result.Source.Should().Be(LauncherVersionSource.DepsJson);
        result.Version.Value.Should().Be(new Version(1, 6, 15));
    }

    // A native launcher binary has no version data the PE reader can use.
    private void WriteStubExecutable() => File.WriteAllBytes(_fixture.Exe.ToString(), [0x4d, 0x5a]);

    [Fact]
    public void DepsJson_Used_WhenNoAssemblyExists()
    {
        _fixture.WriteDepsJson("1.6.15");

        var result = _reader.Read(_fixture.Dll);

        result.Source.Should().Be(LauncherVersionSource.DepsJson);
        result.Version.Value.Should().Be(new Version(1, 6, 15));
    }

    [Fact]
    public void DepsJson_MalformedYieldsNone()
    {
        _fixture.WriteRawDepsJson("{ this is not json");

        var result = _reader.Read(_fixture.Dll);

        result.Source.Should().Be(LauncherVersionSource.None);
        result.Version.HasValue.Should().BeFalse();
    }

    [Fact]
    public void DepsJson_UnversionedProjectIsUnknown()
    {
        _fixture.WriteDepsJson("1.0.0");

        var result = _reader.Read(_fixture.Dll);

        result.Source.Should().Be(LauncherVersionSource.None);
        result.Version.HasValue.Should().BeFalse();
    }

    [Fact]
    public void DepsJson_WithoutProjectLibraryYieldsNone()
    {
        _fixture.WriteRawDepsJson("{}");

        var result = _reader.Read(_fixture.Dll);

        result.Source.Should().Be(LauncherVersionSource.None);
    }

    [Fact]
    public void NothingPresent_YieldsNone()
    {
        var result = _reader.Read(_fixture.Dll);

        result.Source.Should().Be(LauncherVersionSource.None);
        result.Version.HasValue.Should().BeFalse();
    }
}
