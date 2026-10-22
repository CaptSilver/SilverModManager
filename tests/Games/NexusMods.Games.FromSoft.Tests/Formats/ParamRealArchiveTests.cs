using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

namespace NexusMods.Games.FromSoft.Tests.Formats;

/// <summary>
/// Reads the params out of a real mod's regulation. Never part of the default run: it needs a checkout of the erm tool
/// and the mod archive it pinned, and no mod file is checked in. A param that reads but writes back differently means
/// the header re-stamp or the strings shift is wrong, which nothing built from fixtures would show.
/// </summary>
public class ParamRealArchiveTests
{
    private const string ModId = "clevers-moveset";
    private const string SpEffectType = "SP_EFFECT_PARAM_ST";

    private static readonly FromSoftFormatTraits Traits = EldenRingGame.Definition.Formats;

    private static IReadOnlyList<BinderEntry> RealParams()
        => Regulation.Entries(ErmVendorArchive.ReadPinnedMember(ModId, "regulation.bin"), Traits);

    private static (BinderEntry Entry, ParamTable Table) SpEffect()
    {
        foreach (var entry in RealParams())
        {
            if (TryRead(entry) is { } table && table.ParamType == SpEffectType) return (entry, table);
        }
        throw new InvalidOperationException($"{ModId}'s regulation has no {SpEffectType} param");
    }

    private static ParamTable? TryRead(BinderEntry entry)
    {
        try { return Param.Read(entry.Data); }
        catch (ParamException) { return null; } // a layout the reader refuses is not a round-trip claim
    }

    [Fact]
    public void EveryReadableParamRoundTripsByteForByte()
    {
        var entries = RealParams();
        var readable = entries.Where(e => TryRead(e) is not null).ToList();
        readable.Should().NotBeEmpty("a regulation whose params are all unreadable means the container layer is broken");
        readable.Where(e => !Param.Write(Param.Read(e.Data)).AsSpan().SequenceEqual(e.Data)).Select(e => e.Id)
            .Should().BeEmpty("a param that reads but writes back differently has a broken header re-stamp or strings shift");
    }

    [Fact]
    public void PatchingSpEffectRowsLeavesEveryOtherRowUntouched()
    {
        var (_, table) = SpEffect();
        var blob = Enumerable.Repeat((byte)0xA5, table.Stride).ToArray();
        var existing = table.Rows.Select(r => r.Id).ToHashSet();
        var newIds = new[] { 990001, 990002 }.Where(i => !existing.Contains(i)).ToArray();

        var patched = Param.Read(Param.Write(Param.PatchRows(table, insert: newIds.ToDictionary(i => i, _ => blob))));

        patched.Rows.Length.Should().Be(table.Rows.Length + newIds.Length);
        var after = patched.Rows.ToDictionary(r => r.Id, r => r.Data);
        foreach (var row in table.Rows) after[row.Id].Should().Equal(row.Data);
        foreach (var id in newIds) after[id].Should().Equal(blob);
        patched.Rows.Select(r => r.Id).Should().BeInAscendingOrder();
    }

    [Fact]
    public void ARowInsertCostsItsDataPlusItsTableEntry()
    {
        var (entry, table) = SpEffect();
        var existing = table.Rows.Select(r => r.Id).ToHashSet();
        var newIds = new[] { 990001, 990002 }.Where(i => !existing.Contains(i)).ToArray();
        var written = Param.Write(Param.PatchRows(table, insert: newIds.ToDictionary(i => i, _ => new byte[table.Stride])));
        (written.Length - entry.Data.Length).Should().Be(newIds.Length * (table.Stride + 24));
    }
}
