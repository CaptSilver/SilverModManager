using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using Table = SilverModManager.Sdk.ProxyConsole.Table;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod wabbajack inspect</c> over the checked-in real modlist. The verb only reads the file, so the
/// library is never involved.
/// </summary>
public class WabbajackVerbTests(IServiceProvider provider, TemporaryFileManager temporaryFileManager)
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Resources", "MO2AndSKSETest.wabbajack");

    private async Task<(int ExitCode, LoggingRenderer Renderer)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, renderer);
    }

    private static string[] Lines(LoggingRenderer renderer) => renderer.Logs.OfType<Text>().Select(t => string.Format(t.Template, t.Arguments.Cast<object>().ToArray()).TrimEnd('\n')).ToArray();

    private static string[][] Cells(Table table)
        => table.Rows.Select(row => row.Select(c => c is Text text ? text.Template : "").ToArray()).ToArray();

    [Fact]
    public async Task InspectPrintsTheListsHeaderAndCountsPerKind()
    {
        var (exitCode, renderer) = await Run("wabbajack", "inspect", "-f", Fixture);

        exitCode.Should().Be(0);
        Lines(renderer).Should().Contain(["Name: MO2AndSKSETest", "Version: 1.0.0.0", "GameType: SkyrimSpecialEdition"]);
        Lines(renderer).Should().Contain(l => l.StartsWith("Author:"));

        var tables = renderer.Logs.OfType<Table>().ToArray();
        tables.Should().HaveCount(3);
        Cells(tables[0]).Should().BeEquivalentTo(new[]
        {
            new[] { "FromArchive", "1395" },
            new[] { "InlineFile", "11" },
            new[] { "ArchiveMeta", "2" },
            new[] { "RemappedInlineFile", "1" },
        });
        Cells(tables[1]).Should().BeEquivalentTo(new[] { new[] { "Http", "2" } });
    }

    [Fact]
    public async Task InspectListsEachArchiveWithItsSizeAndSource()
    {
        var (exitCode, renderer) = await Run("wabbajack", "inspect", "-f", Fixture);

        exitCode.Should().Be(0);
        var archives = Cells(renderer.Logs.OfType<Table>().Last());
        archives.Should().BeEquivalentTo(new[]
        {
            new[] { "Mod.Organizer-2.4.2.7z", "89503525", "Http", "https://github.com/ModOrganizer2/modorganizer/releases/download/v2.4.2/Mod.Organizer-2.4.2.7z" },
            new[] { "skse64_2_00_19.7z", "922047", "Http", "https://skse.silverlock.org/beta/skse64_2_00_19.7z" },
        });
    }

    [Fact]
    public async Task InspectRefusesAFileThatIsNotAModlist()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var path = folder.Path / "not-a-modlist.wabbajack";
        await using (var stream = File.Create(path.ToString()))
        await using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            await using (var entry = zip.CreateEntry("something-else").Open())
                await entry.WriteAsync("x"u8.ToArray(), TestContext.Current.CancellationToken);

        var (exitCode, renderer) = await Run("wabbajack", "inspect", "-f", path.ToString());

        exitCode.Should().NotBe(0);
        Lines(renderer).Should().ContainSingle(l => l.Contains("no 'modlist' entry"));
    }

    [Fact]
    public async Task InspectFailsOnAMissingFile()
    {
        var (exitCode, renderer) = await Run("wabbajack", "inspect", "-f", Path.Combine(Fixture, "..", "nope.wabbajack"));

        exitCode.Should().NotBe(0);
        Lines(renderer).Should().ContainSingle(l => l.Contains("does not exist"));
    }
}
