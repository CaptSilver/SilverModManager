using FluentAssertions;
using SilverModManager.Abstractions.Diagnostics;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

public class OpenMwConfigEmitterTests(ITestOutputHelper helper) : AOpenMwGameTest<OpenMwConfigEmitterTests>(helper)
{
    private const string Source = "SilverModManager.Games.CreationEngine";

    protected override string ConfigText => Install.Config("content=Gone.esp\ncontent=Elsewhere.esp\ncontent=Local.esp\ngroundcover=Grass.esp\n");

    protected override string[] PluginsInMorrowind => ["Elsewhere.esp"];

    protected override string[] PluginsInDataLocal => ["Local.esp"];

    private async Task<List<Diagnostic>> OpenMwDiagnostics()
    {
        var loadout = await CreateLoadout();
        return (await Game.Diagnose(Refresh(loadout))).Where(d => d.Id.Source == Source && (d.Id.Number == 14 || d.Id.Number == 16)).ToList();
    }

    [Fact]
    public async Task AContentNameNoDirectoryHasIsCritical()
    {
        var dangling = (await OpenMwDiagnostics()).Should().ContainSingle(d => d.Id.Number == 14).Subject;

        dangling.Severity.Should().Be(DiagnosticSeverity.Critical);
        var summary = dangling.FormatSummary(new PlainDiagnosticWriter());
        summary.Should().Contain("Gone.esp").And.Contain("Grass.esp").And.NotContain("Elsewhere.esp").And.NotContain("Local.esp").And.NotContain("builtin.omwscripts");
        dangling.FormatDetails(new PlainDiagnosticWriter()).Should().Contain(Config.GetFullPath());
    }

    [Fact]
    public async Task ADeployedBsaWithoutAFallbackArchiveLineWarns()
    {
        var loadout = await CreateLoadout();
        var archive = await CreateTestArchive(new Dictionary<NexusMods.Paths.RelativePath, byte[]> { { "Data Files/Meshes.bsa", "bsa"u8.ToArray() } });
        var library = await RegisterLocalArchive(archive.Path);
        await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);

        var unregistered = (await Game.Diagnose(Refresh(loadout))).Should().ContainSingle(d => d.Id.Source == Source && d.Id.Number == 16).Subject;

        unregistered.Severity.Should().Be(DiagnosticSeverity.Warning);
        unregistered.FormatSummary(new PlainDiagnosticWriter()).Should().Contain("Meshes.bsa");
    }

    [Fact]
    public async Task ADeployedBsaThatIsRegisteredIsNotReported()
    {
        Roots.WriteConfig(0, ConfigText + "fallback-archive=meshes.BSA\n");
        var loadout = await CreateLoadout();
        var archive = await CreateTestArchive(new Dictionary<NexusMods.Paths.RelativePath, byte[]> { { "Data Files/Meshes.bsa", "bsa"u8.ToArray() } });
        var library = await RegisterLocalArchive(archive.Path);
        await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);

        (await Game.Diagnose(Refresh(loadout))).Should().NotContain(d => d.Id.Source == Source && d.Id.Number == 16);
    }
}

public class OpenMwStockConfigEmitterTests(ITestOutputHelper helper) : AOpenMwGameTest<OpenMwStockConfigEmitterTests>(helper)
{
    [Fact]
    public async Task NothingIsReportedOnAStockConfig()
    {
        var loadout = await CreateLoadout();

        (await Game.Diagnose(Refresh(loadout))).Should().NotContain(d => d.Id.Source == "SilverModManager.Games.CreationEngine" && (d.Id.Number == 14 || d.Id.Number == 16));
    }
}
