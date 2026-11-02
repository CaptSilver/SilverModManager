using System.Buffers.Binary;
using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using Table = SilverModManager.Sdk.ProxyConsole.Table;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod textures census</c> over archives built in the test: it reads each .dds header and prints a count per
/// layout, plus the headers it could not read.
/// </summary>
public class TexturesCensusVerbTests(IServiceProvider provider, TemporaryFileManager temporaryFileManager)
{
    private async Task<(int ExitCode, LoggingRenderer Renderer)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, renderer);
    }

    private static string[][] Cells(Table table)
        => table.Rows.Select(row => row.Select(c => c is Text text ? string.Format(text.Template, text.Arguments.Cast<object>().ToArray()) : "").ToArray()).ToArray();

    private static byte[] Dds(string fourCc, int pixelBytes)
    {
        var bytes = new byte[128 + pixelBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 0x4);
        System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(bytes, 84);
        return bytes;
    }

    private static void AddZip(AbsolutePath path, params (string Name, byte[] Bytes)[] entries)
    {
        using var zip = ZipFile.Open(path.ToString(), ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    }

    [Fact]
    public async Task CountsEachLayoutAndNamesTheHeaderItCouldNotRead()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        AddZip(folder.Path / "first.zip",
            ("textures/a.dds", Dds("DXT5", 16)),
            ("textures/b.dds", Dds("DXT5", 16)),
            ("readme.txt", "not a texture"u8.ToArray()));
        AddZip(folder.Path / "second.zip",
            ("textures/c.dds", Dds("ATI2", 16)),
            ("textures/broken.dds", Dds("????", 16)));

        var (exitCode, renderer) = await Run("textures", "census", "-d", folder.Path.ToString());

        exitCode.Should().Be(0);
        var tables = renderer.Logs.OfType<Table>().ToArray();
        tables.Should().HaveCount(2);
        Cells(tables[0]).Should().BeEquivalentTo(new[] { new[] { "BC3_UNORM", "2" }, new[] { "BC5_UNORM", "1" } });
        var unreadable = Cells(tables[1]);
        unreadable.Should().ContainSingle();
        unreadable[0][0].Should().Be("second.zip: textures/broken.dds");
        unreadable[0][1].Should().Contain("fourcc=");
    }

    [Fact]
    public async Task ReportsAnEmptyDirectoryWithoutTables()
    {
        await using var folder = temporaryFileManager.CreateFolder();

        var (exitCode, renderer) = await Run("textures", "census", "-d", folder.Path.ToString());

        exitCode.Should().Be(0);
        renderer.Logs.OfType<Table>().Should().BeEmpty();
    }
}
