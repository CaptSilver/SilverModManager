using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

namespace NexusMods.Games.FromSoft.Tests.Formats;

/// <summary>
/// Reads and rewrites the t000001000 state machine out of two real mods' talk binders. Never part of the default run:
/// it needs a checkout of the erm tool and the mod archives it pinned, and no mod file is checked in. The two files
/// are the class the merge actually produces, so a byte-exact round trip is the bar.
/// </summary>
public class EsdRealArchiveTests
{
    private const string TalkBinder = "script/talk/m00_00_00_00.talkesdbnd.dcx";

    // Properties of the blobs, not of the format: repinning either mod means re-deriving them.
    // (groups, states including each group's dummy, conditions, calls, args)
    public static TheoryData<string, int, int, int, int, int> Mods => new()
    {
        { "boss-resurrection-lite", 136, 1650, 2076, 1602, 3465 },
        { "journey-with-melina", 87, 1032, 1208, 669, 1447 },
    };

    private static byte[] RealEsd(string modId)
    {
        var traits = EldenRingGame.Definition.Formats;
        var container = ErmVendorArchive.ReadPinnedMember(modId, TalkBinder);
        var entries = Binders.For(traits).Read(Dcx.Read(container, traits));
        return entries.Single(e => e.Id == 0).Data;
    }

    [Theory]
    [MemberData(nameof(Mods))]
    public void ReadReachesExactlyTheCountsTheHeaderDeclares(string modId, int groups, int states, int conditions, int calls, int args)
    {
        var parsed = Esd.Read(RealEsd(modId));
        parsed.Groups.Should().HaveCount(groups);
        (parsed.Groups.Sum(g => g.States.Count) + parsed.Groups.Count).Should().Be(states);

        // Shared condition rows are read once per reference, so identity is the row's order.
        var seen = parsed.Groups.SelectMany(g => g.States).SelectMany(s => EsdFixture.WalkConditions(s.Conditions))
            .GroupBy(c => c.Order).Select(g => g.First()).ToList();
        seen.Should().HaveCount(conditions);
        var seenCalls = parsed.Groups.SelectMany(g => g.States).SelectMany(s => s.Entry.Concat(s.Exit).Concat(s.While))
            .Concat(seen.SelectMany(c => c.PassCommands)).GroupBy(c => c.Order).Select(g => g.First()).ToList();
        seenCalls.Should().HaveCount(calls);
        seenCalls.Sum(c => c.Args.Count).Should().Be(args);
    }

    [Theory]
    [MemberData(nameof(Mods))]
    public void WriteRoundTripsTheModFileByteForByte(string modId, int groups, int states, int conditions, int calls, int args)
    {
        _ = (groups, states, conditions, calls, args);
        var raw = RealEsd(modId);
        Esd.Write(Esd.Read(raw)).Should().Equal(raw);
    }
}
