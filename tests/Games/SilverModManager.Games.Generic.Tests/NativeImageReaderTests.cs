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
    public void DelayLoadedLibrariesAreReadFromTheirOwnTable()
    {
        var file = Write("game.exe", SyntheticPe.Build(["kernel32.dll"], delayImports: ["openvr_api.dll", "xinput1_3.dll"]));

        var imports = _reader.ReadImports(file);

        imports.DelayLoaded.Should().Equal("openvr_api.dll", "xinput1_3.dll");
        imports.Libraries.Should().Equal("kernel32.dll");
    }

    [Fact]
    public void OldStyleDelayLoadNamesAreReadFromTheirVirtualAddress()
    {
        var file = Write("old.exe", SyntheticPe.Build(["kernel32.dll"], delayImports: ["d3d8.dll"], is64Bit: false, delayNamesAreAddresses: true));

        _reader.ReadImports(file).DelayLoaded.Should().Equal("d3d8.dll");
    }

    [Fact]
    public void AnImageWithoutADelayTableHasNoDelayLoadedLibraries()
    {
        _reader.ReadImports(Write("plain.exe", SyntheticPe.Build(["kernel32.dll"]))).DelayLoaded.Should().BeEmpty();
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
        var file = Write("x86.dll", SyntheticPe.Build([], version: new Version(14, 0, 24215, 1), is64Bit: false));

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
        var file = Write("msvcp140.dll", SyntheticPe.Build([], version: new Version(14, 0, 24215, 1)));

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
