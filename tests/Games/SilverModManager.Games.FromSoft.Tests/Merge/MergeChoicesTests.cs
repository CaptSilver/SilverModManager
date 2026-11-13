using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

public class MergeChoicesTests(ITestOutputHelper helper) : AVanillaDumpTest<MergeChoicesTests>(helper)
{
    private const string Msg = "msg/engus/item.msgbnd.dcx";

    private static byte[] Msgbnd(string text, int ownId)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>>
        {
            [1] = new Dictionary<int, string?> { [10] = text, [ownId] = "own" },
        }, Formats);

    private MergePathIndex IndexOf(Loadout.ReadOnly loadout)
        => ServiceProvider.GetRequiredService<LoadoutMergeSources>().Index(Refresh(loadout));

    private async Task Pin(Loadout.ReadOnly loadout, string path, string key, LoadoutItemGroupId group, uint? modId = null)
    {
        using var tx = Connection.BeginTransaction();
        var row = new LoadoutMergeChoice.New(tx) { LoadoutId = loadout.LoadoutId, Path = path, Key = key, WinnerGroupId = group };
        if (modId is { } id) row.WinnerModId = id;
        await tx.Commit();
    }

    [Fact]
    public async Task For_ResolvesAGroupPinToTheContributorName()
    {
        var loadout = await CreateLoadout();
        var alpha = await InstallPackage(loadout.LoadoutId, "Alpha", (Msg, Msgbnd("alpha text", 11)));
        await InstallPackage(loadout.LoadoutId, "Beta", (Msg, Msgbnd("beta text", 12)));
        await Pin(loadout, Msg, "fmg:1/10", alpha.LoadoutItemGroupId);

        var choices = MergeChoices.For(Connection.Db, loadout.LoadoutId, IndexOf(loadout));

        var path = choices.Should().ContainKey(Msg).WhoseValue;
        path.Winner("fmg:1/10").Should().Be(alpha.AsLoadoutItem().Name);
        path.All.Should().ContainSingle().Which.Should().Be(("fmg:1/10", alpha.AsLoadoutItem().Name));
        path.Stale.Should().BeEmpty();
    }

    [Fact]
    public async Task For_ResolvesANexusModIdPinAcrossAReinstall()
    {
        var loadout = await CreateLoadout();
        var alpha = await InstallPackage(loadout.LoadoutId, "Alpha", (Msg, Msgbnd("alpha text", 11)));
        await InstallPackage(loadout.LoadoutId, "Beta", (Msg, Msgbnd("beta text", 12)));
        await LinkToNexusMod(alpha, 777);

        // The group the pin was made against is gone (a reinstall makes a new one), so only the Nexus mod id still names Alpha.
        var gone = await InstallPackage(loadout.LoadoutId, "Gone", ("msg/engus/menu.msgbnd.dcx", Msgbnd("gone text", 13)));
        await Pin(loadout, Msg, "fmg:1/10", gone.LoadoutItemGroupId, modId: 777);

        var choices = MergeChoices.For(Connection.Db, loadout.LoadoutId, IndexOf(loadout));

        choices[Msg].Winner("fmg:1/10").Should().Be(alpha.AsLoadoutItem().Name);
        choices[Msg].Stale.Should().BeEmpty();
    }

    [Fact]
    public async Task For_MarksAPinOnANonContributorStale()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout.LoadoutId, "Alpha", (Msg, Msgbnd("alpha text", 11)));
        await InstallPackage(loadout.LoadoutId, "Beta", (Msg, Msgbnd("beta text", 12)));
        var gamma = await InstallPackage(loadout.LoadoutId, "Gamma", ("msg/engus/menu.msgbnd.dcx", Msgbnd("gamma text", 13)));
        await Pin(loadout, Msg, "fmg:1/10", gamma.LoadoutItemGroupId);

        var choices = MergeChoices.For(Connection.Db, loadout.LoadoutId, IndexOf(loadout));

        var path = choices[Msg];
        path.Winner("fmg:1/10").Should().BeNull();
        path.All.Should().BeEmpty();
        path.Stale.Should().ContainSingle().Which.Should().Be(("fmg:1/10", $"{gamma.AsLoadoutItem().Name} no longer provides {Msg}"));
    }

    private async Task LinkToNexusMod(LoadoutItemGroup.ReadOnly group, uint modId)
    {
        using var tx = Connection.BeginTransaction();
        var item = ShareableMods.AddNexusItem(tx, "Alpha", NexusModsGameId.From(1704), GameDomain.From("eldenring"), modId, fileId: 1);
        var result = await tx.Commit();

        using var relink = Connection.BeginTransaction();
        relink.Add(group.Id, LibraryLinkedLoadoutItem.LibraryItem, result[item]);
        await relink.Commit();
    }
}
