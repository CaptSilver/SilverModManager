using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.Generic.Diagnostics;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// The PE reader behind the Visual C++ runtime check, run on images built in code.
/// </summary>
public sealed class NativeImageReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(TestScratch.Combine("native-image-reader", Guid.NewGuid().ToString("N"))).FullName;
    private readonly NativeImageReader _reader = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AbsolutePath Write(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return FileSystem.Shared.FromUnsanitizedFullPath(path);
    }

    [Fact]
    public void ImportedLibrariesAreTheNamesInTheImportTable()
    {
        var file = Write("openvr_api.dll", SyntheticPe.Build(["MSVCP140.dll", "KERNEL32.dll", "VCRUNTIME140.dll"]));

        _reader.ReadImports(file).Libraries.Should().Equal("MSVCP140.dll", "KERNEL32.dll", "VCRUNTIME140.dll");
    }

    [Fact]
    public void A64BitImageIsReportedAs64Bit()
    {
        _reader.ReadImports(Write("x64.dll", SyntheticPe.Build(["KERNEL32.dll"]))).Is64Bit.Should().BeTrue();
    }

    [Fact]
    public void A32BitImageIsReportedAs32BitWithItsImports()
    {
        var file = Write("x86.dll", SyntheticPe.Build(["MSVCP140.dll", "KERNEL32.dll"], is64Bit: false));

        var imports = _reader.ReadImports(file);

        imports.Is64Bit.Should().BeFalse();
        imports.Libraries.Should().Equal("MSVCP140.dll", "KERNEL32.dll");
    }

    [Fact]
    public void A32BitImageHasAVersion()
    {
        var file = Write("x86.dll", SyntheticPe.Build([], new Version(14, 0, 24215, 1), is64Bit: false));

        _reader.FileVersion(file).Should().Be(new Version(14, 0, 24215, 1));
    }

    [Fact]
    public void AnImageWithoutImportsHasNone()
    {
        _reader.ReadImports(Write("empty.dll", SyntheticPe.Build([]))).Libraries.Should().BeEmpty();
    }

    [Fact]
    public void FileVersionIsTheFixedFileInfoVersion()
    {
        var file = Write("msvcp140.dll", SyntheticPe.Build([], new Version(14, 0, 24215, 1)));

        _reader.FileVersion(file).Should().Be(new Version(14, 0, 24215, 1));
    }

    [Fact]
    public void AnImageWithoutAVersionResourceHasNoVersion()
    {
        _reader.FileVersion(Write("placeholder.dll", SyntheticPe.Build(["KERNEL32.dll"]))).Should().BeNull();
    }

    [Fact]
    public void AFileThatIsNotAWindowsImageReadsAsEmpty()
    {
        var file = Write("notes.dll", "not a dll"u8.ToArray());

        _reader.ReadImports(file).Libraries.Should().BeEmpty();
        _reader.FileVersion(file).Should().BeNull();
    }
}
