using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Install;

namespace SilverModManager.Wabbajack.Tests.Models;

/// <summary>Which skipped files keep an installed list from being finished.</summary>
public class WabbajackSkippedFileTests(ITestOutputHelper helper) : InstallTestBase<WabbajackSkippedFileTests>(helper)
{
    [Fact]
    public async Task SkipsThatKeepTheListOff_LeaveOutEveryByDesignKind()
    {
        var loadout = await CreateLoadout();
        var list = new ListBuilder();
        list.Directives.Add(list.Inline("mods/A/a.txt", "a"));
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var (_, group) = await Install(loadout, modlist, new InstallOptions(false, new HashSet<RelativePath>()));

        using (var tx = Connection.BeginTransaction())
        {
            foreach (var kind in Enum.GetValues<SkipKind>())
            {
                _ = new WabbajackSkippedFile.New(tx, out var id)
                {
                    LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, Name = kind.ToString() },
                    GroupId = WabbajackModlistLoadoutGroupId.From(group.Id),
                    To = $"x/{kind}.txt",
                    Reason = "test",
                    Kind = kind,
                };
            }
            await tx.Commit();
        }

        var keepingItOff = WabbajackSkippedFile.SkipsThatKeepTheListOff(Connection.Db, group.Id).Select(r => r.Kind).ToArray();

        keepingItOff.Should().BeEquivalentTo(Enum.GetValues<SkipKind>().Where(kind => !kind.IsByDesign()));
        keepingItOff.Should().NotContain([SkipKind.KeptOriginal, SkipKind.ToolFolder, SkipKind.GameFileOwnVersion]);
        keepingItOff.Should().Contain([SkipKind.GameFileVersion, SkipKind.MissingArchive], "a build that reads a differing game file produced nothing");
    }
}
