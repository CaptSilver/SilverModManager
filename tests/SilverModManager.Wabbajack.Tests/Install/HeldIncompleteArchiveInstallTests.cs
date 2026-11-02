using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Acquire;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>
/// A held archive that lacks the bytes of a nested archive the list copies whole still serves every other file the
/// list reads from it. Only the directive that needs the missing bytes is skipped.
/// </summary>
public class HeldIncompleteArchiveInstallTests(ITestOutputHelper helper) : InstallTestBase<HeldIncompleteArchiveInstallTests>(helper)
{
    private static readonly RelativePath Plain = P("docs/readme.txt");

    [Fact]
    public async Task AnIncompleteHeldArchiveWhoseSourceIsGoneStillInstallsItsOtherFiles()
    {
        await using var zip = await BuildNestedZip();
        var readme = "read me"u8.ToArray();
        await AddWithoutNestedBytes(zip, (Plain, readme));
        var list = new ListBuilder();
        // A source the app cannot fetch from, so the archive can never be completed by a download.
        list.Archives.Add(new ArchiveEntry(zip.OuterHash, zip.OuterBytes.Length, "outer.zip", "", new ManualState(new Uri("https://example.test/m"), "get it")));
        list.Directives.Add(new FromArchive(P("mods/A/readme.txt"), Xx64.Of(readme), readme.Length, new ArchiveHashPath(zip.OuterHash, [Plain])));
        list.Directives.Add(new FromArchive(P("mods/A/inner.zip"), Xx64.Of(zip.InnerBytes), zip.InnerBytes.Length, new ArchiveHashPath(zip.OuterHash, [InnerZip])));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var (_, group) = await Install(loadout, modlist, new InstallOptions(SkipMissingArchives: true, new HashSet<RelativePath>()));

        TreeOf(group)["A"].Should().ContainKey("Game/Data/readme.txt");
        var skipped = SkipsOf(group).Should().ContainSingle("only the nested archive's copy lacks its bytes").Subject;
        skipped.Kind.Should().Be(SkipKind.MemberNotFound);
        skipped.To.Should().Be("mods/A/inner.zip");
    }
}
