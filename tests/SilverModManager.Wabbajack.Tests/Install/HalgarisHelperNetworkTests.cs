using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Install;
using NexusMods.Wabbajack.Models;

namespace NexusMods.Wabbajack.Tests.Install;

/// <summary>
/// Installs a small real list, Halgari's Helper, from the Wabbajack CDN into a stubbed Skyrim SE. It downloads about
/// 100 MB, so it only runs when <c>WABBAJACK_NETWORK_TESTS=1</c>.
/// </summary>
public class HalgarisHelperNetworkTests(ITestOutputHelper helper) : InstallTestBase<HalgarisHelperNetworkTests>(helper)
{
    private static readonly Uri ListUrl = new("https://authored-files.wabbajack.org/HalgarisHelper.wabbajack_5d55cc2d-2dbd-49ba-82d5-a66fb0572c54");
    private const string ListHash = "83fJglT61Dk=";
    private const long ListSize = 1588954;
    private const string ArchiveHash = "LqKnmSUrXac=";
    private const long ArchiveSize = 90051560;

    private static void RequireNetworkTests()
        => Assert.SkipUnless(Environment.GetEnvironmentVariable("WABBAJACK_NETWORK_TESTS") == "1", "WABBAJACK_NETWORK_TESTS is not set to 1");

    [Fact]
    [Trait("RequiresNetworking", "True")]
    public async Task HalgarisHelperInstallsIntoAStubbedSkyrimSe()
    {
        RequireNetworkTests();
        var token = TestContext.Current.CancellationToken;

        // The definition and parts are fetched by the CDN job; a plain GET of the address returns an HTML stub.
        await using var folder = TemporaryFileManager.CreateFolder();
        var path = folder.Path / "HalgarisHelper.wabbajack";
        var downloaded = await WabbajackCdnDownloadJob.Create(ServiceProvider, ListUrl, path);
        downloaded.FileExists.Should().BeTrue();
        new FileInfo(downloaded.ToString()).Length.Should().Be(ListSize);
        await using (var stream = File.OpenRead(downloaded.ToString()))
            WabbajackHash.ToBase64(await Xx64Hasher.HashAsync(stream, cancellationToken: token)).Should().Be(ListHash);

        var added = await LibraryService.AddLocalFile(downloaded);
        added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out var modlist).Should().BeTrue();
        modlist.GameType.Should().Be("SkyrimSpecialEdition");
        modlist.ArchiveCount.Should().Be(1);

        var list = await ReadList(modlist);
        list.Directives.Should().HaveCount(1512);
        list.Directives.Count(d => d.GetType() == typeof(FromArchive)).Should().Be(1497);
        list.Directives.Count(d => d.GetType() == typeof(InlineFile)).Should().Be(12);
        list.Directives.Count(d => d.GetType() == typeof(RemappedInlineFile)).Should().Be(3);

        var loadout = await CreateLoadout();
        var plan = await ServiceProvider.GetRequiredService<ArchiveInventory>().Build(list, Connection.Db, loadout, token);
        var row = plan.Rows.Should().ContainSingle().Subject;
        row.Archive.XxHash64.Should().Be(WabbajackHash.Parse(ArchiveHash));
        row.Archive.Size.Should().Be(ArchiveSize);
        row.State.Should().Be(ArchiveState.Automatic);
        row.Archive.State.Should().BeOfType<HttpState>();

        var acquirer = ServiceProvider.GetRequiredService<IArchiveAcquirer>();
        await acquirer.Run(plan, null, token);
        acquirer.Plan.Rows.Should().ContainSingle().Which.State.Should().Be(ArchiveState.Have, acquirer.Plan.Rows[0].Reason);

        var (job, group) = await Install(loadout, modlist, new InstallOptions(false, new HashSet<RelativePath>()));

        SkipsOf(group).Should().BeEmpty();
        job.Progress.DroppedCount.Should().BeGreaterThan(0, "the Mod Organizer files and folders are not installed");

        var tree = TreeOf(group);
        tree.Keys.Should().NotContain("mods", "the list's mods\\ folder is not a group of its own");
        var targets = tree.Values.SelectMany(files => files.Keys).ToArray();
        foreach (var rootFile in list.Directives.Select(d => d.To).Where(to => to.Depth == 1))
            targets.Should().NotContain(t => t.EndsWith("/" + rootFile.FileName.ToString(), StringComparison.OrdinalIgnoreCase) && !t.StartsWith("Preferences/"),
                $"the root-level file {rootFile} belongs to Mod Organizer's instance");
        // Targets read "<location>/<path>", so the folder check looks past the location.
        var targetPaths = targets.Select(t => t[(t.IndexOf('/') + 1)..]).ToArray();
        foreach (var mo2Folder in new[] { "profiles", "downloads", "mods", "overwrite" })
            targetPaths.Should().NotContain(t => t.StartsWith(mo2Folder + "/", StringComparison.OrdinalIgnoreCase), $"{mo2Folder}\\ is one of Mod Organizer's folders");

        tree["Profile settings"].Keys.Should().Contain(["Preferences/Skyrim.ini", "Preferences/SkyrimPrefs.ini", "Preferences/SkyrimCustom.ini"]);

        // The stub game folder and the test's prefix are the only places the job could have read or written.
        var gameRoot = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));
        var extras = ExtrasOf(group);
        extras.ToString().Should().NotStartWith(gameRoot.ToString());
        extras.ToString().Should().Contain(Startup.SettingsRoot, "extras stay under the test's own settings folder");

        helper.WriteLine("wabbajack inspect output:");
        foreach (var line in InspectOutput(list)) helper.WriteLine(line);
    }

    private async Task<ModList> ReadList(WabbajackModlistLibraryFile.ReadOnly modlist)
    {
        modlist.AsLibraryFile().TryGetAsLibraryArchive(out var archive).Should().BeTrue();
        return await ModlistArchive.ReadModlist(FileStore, archive);
    }

    private static IEnumerable<string> InspectOutput(ModList list)
    {
        yield return $"Name: {list.Name}, Author: {list.Author}, Version: {list.Version}, GameType: {list.GameType}";
        foreach (var (kind, count) in Cli.ModlistInspection.DirectiveKinds(list)) yield return $"directive {kind}: {count}";
        foreach (var (kind, count) in Cli.ModlistInspection.SourceKinds(list)) yield return $"source {kind}: {count}";
        foreach (var archive in list.Archives) yield return string.Join(" | ", Cli.ModlistInspection.ArchiveRow(archive));
    }
}
