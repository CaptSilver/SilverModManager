using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Acquire;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>What an install tells the log about the files it skipped, which the Health Check and the modlist page only show later.</summary>
public class InstallSkipLoggingTests(ITestOutputHelper helper) : InstallTestBase<InstallSkipLoggingTests>(helper)
{
    private static readonly InstallOptions Defaults = new(false, new HashSet<RelativePath>());

    private readonly ListLogger<InstallModlistJob> _log = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<ILogger<InstallModlistJob>>(_log);

    [Fact]
    public async Task EachSkipIsWarnedAboutOnceWithTheListTheTargetTheKindAndTheReason()
    {
        var archive = await AddZip("a.zip", new() { ["t.dds"] = Bc1Dds(8) });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        list.Directives.Add(UnwritableTexture("mods/A/t.dds", archive, "t.dds"));
        var loadout = await CreateLoadout();

        await Install(loadout, await AddModlist(list.ToList("My List"), list.Blobs), Defaults);

        var warning = _log.Messages(LogLevel.Warning).Where(m => m.Contains("mods/A/t.dds")).Should().ContainSingle().Subject;
        warning.Should().Contain("My List").And.Contain("Transform").And.Contain("could not be re-encoded");
    }

    [Fact]
    public async Task SkipsAreSummarisedPerKindWithTheCountAndAtMostFiveExamples()
    {
        var archive = await AddZip("a.zip", new() { ["t.dds"] = Bc1Dds(8) });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        for (var i = 0; i < 7; i++) list.Directives.Add(UnwritableTexture($"mods/A/t{i}.dds", archive, "t.dds"));
        var loadout = await CreateLoadout();

        await Install(loadout, await AddModlist(list.ToList("My List"), list.Blobs), Defaults);

        var summary = _log.Messages(LogLevel.Information).Where(m => m.Contains("skipped as Transform")).Should().ContainSingle().Subject;
        summary.Should().Contain("7 file(s)").And.Contain("My List");
        Enumerable.Range(0, 7).Count(i => summary.Contains($"mods/A/t{i}.dds")).Should().Be(5);
    }

    [Fact]
    public async Task AnArchiveThatWasNotBuiltLogsTheInputsItTookWithIt()
    {
        var list = new ListBuilder();
        list.Directives.Add(list.Inline("TEMP_BSA_FILES/pack/one.txt", "1"));
        list.Directives.Add(list.Inline("TEMP_BSA_FILES/pack/two.txt", "2"));
        list.Directives.Add(new CreateBsa(P("mods/A/pack.bsa"), Xx64.From(1), 2, P("pack"),
            System.Text.Json.JsonDocument.Parse("{\"$type\":\"NotAnArchive\"}").RootElement, []));
        var loadout = await CreateLoadout();

        await Install(loadout, await AddModlist(list.ToList("My List"), list.Blobs), Defaults);

        var dropped = _log.Messages(LogLevel.Warning).Where(m => m.Contains("Dropped")).Should().ContainSingle().Subject;
        dropped.Should().Contain("mods/A/pack.bsa").And.Contain("one.txt").And.Contain("two.txt").And.Contain("NotAnArchive");
    }

    [Fact]
    public async Task InputsOfAnArchiveNoDirectiveBuildsAreLogged()
    {
        var list = new ListBuilder();
        list.Directives.Add(list.Inline("TEMP_BSA_FILES/orphan/one.txt", "1"));
        var loadout = await CreateLoadout();

        await Install(loadout, await AddModlist(list.ToList("My List"), list.Blobs), Defaults);

        _log.Messages(LogLevel.Warning).Should().ContainSingle(m => m.Contains("Dropped") && m.Contains("orphan") && m.Contains("one.txt"));
    }

    [Fact]
    public async Task ATextureThatFailsInsideAnArchiveNamesItsMemberPathAndFormat()
    {
        var archive = await AddZip("a.zip", new() { ["t.dds"] = Bc1Dds(8) });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        list.Directives.Add(new TransformedTexture(P("TEMP_BSA_FILES/pack/textures/t.dds"), Xx64.From(1), 1,
            new ArchiveHashPath(archive.XxHash64, [P("t.dds")]), new ImageState(4, 4, 1, "BC6H_UF16")));
        list.Directives.Add(list.Bsa("mods/A/pack.bsa", "pack", "textures/t.dds"));
        var loadout = await CreateLoadout();

        var (_, group) = await Install(loadout, await AddModlist(list.ToList(), list.Blobs), Defaults);

        var skip = SkipsOf(group).Should().ContainSingle().Subject;
        skip.To.Should().Be("mods/A/pack.bsa");
        skip.Reason.Should().Contain("textures/t.dds").And.Contain("BC6H_UF16").And.NotContain("TEMP_BSA_FILES");
    }
}
