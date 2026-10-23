using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

/// <summary>
/// Rebuilds a real message binder unchanged. Never part of the default run: it needs a checkout of the erm tool and
/// the mod archive it pinned, and no mod or game file is checked in. A real binder has the populated hash table and
/// the true entry layout, which the synthetic fixture only approximates.
/// </summary>
public class Bnd4RealArchiveTests
{
    private const string ModId = "clevers-moveset";
    private static readonly string[] Members = ["msg/engus/item_dlc02.msgbnd.dcx", "msg/engus/menu_dlc02.msgbnd.dcx"];

    [Fact]
    public void RebuildingARealMessageBinderWithNoChangesReturnsItUnchanged()
    {
        var dcx = ErmVendorArchive.ReadPinnedMember(ModId, Members);

        var traits = EldenRingGame.Definition.Formats;
        var payload = Dcx.Read(dcx, traits);
        var binder = Binders.For(traits);

        var entries = binder.Read(payload);
        entries.Should().NotBeEmpty();
        binder.Rebuild(payload, new Dictionary<int, byte[]>()).Should().Equal(payload);

        // An empty replacement set returns before any re-layout happens; handing every member back its own bytes
        // runs the full clone over the real layout's gaps and padding.
        binder.Rebuild(payload, entries.ToDictionary(e => e.Id, e => e.Data)).Should().Equal(payload);
    }
}
