using FluentAssertions;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Sdk.Hashes;
using NexusMods.Hashing.xxHash3;

namespace SilverModManager.Games.FromSoft.Tests.Merge;

/// <summary>
/// Pins decide a contested message through the real union strategy. Three mods fold in priority order (A, then B, then C),
/// so a pin on the middle or lowest mod only shows up after the whole fold has run.
/// </summary>
public class MergePlannerChoiceTests
{
    private const string Path = "msg/engus/item.msgbnd.dcx";
    private const string Key = "fmg:1/10";

    private static FromSoftFormatTraits T => EldenRingGame.Definition.Formats;

    private static byte[] Msgbnd(string text, int ownId)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>>
        {
            [1] = new Dictionary<int, string?> { [10] = text, [ownId] = $"own {ownId}" },
        }, T);

    private static ProviderFile File(string mod, ulong priority)
        => new(mod, Path, FileIdentity.XxHash3(Hash.From((ulong)mod.GetHashCode())), priority, mod);

    private static readonly Dictionary<string, byte[]> Contents = new()
    {
        ["A"] = Msgbnd("a's text", 11),
        ["B"] = Msgbnd("b's text", 12),
        ["C"] = Msgbnd("c's text", 13),
        ["D"] = Msgbnd(null!, 14),
    };

    private static MergedPath Plan(PathChoices? pins, params string[] modsHighestFirst)
    {
        var files = modsHighestFirst.Select((m, i) => File(m, (ulong)(modsHighestFirst.Length - i))).ToArray();
        var index = MergePathIndex.Build(files, T, new HashSet<string>());
        var choices = pins is null ? null : new Dictionary<string, PathChoices> { [Path] = pins };
        var plan = MergePlanner.Plan(index, f => Contents[f.PackageId], (_, _) => null, p => throw new InvalidOperationException(p), T, choices);
        plan.Refusal.Should().BeNull();
        return plan.Merged.Single();
    }

    private static string TextOf(MergedPath merged) => MsgbndFixture.ReadBack(merged.Bytes, T)[1][10]!;

    private static PathChoices Pin(string winner) => new([(Key, winner)]);

    [Fact]
    public void ThreeMods_ChoiceForTheLowest_Wins()
    {
        var merged = Plan(Pin("C"), "A", "B", "C");

        TextOf(merged).Should().Be("c's text");
    }

    [Fact]
    public void ThreeMods_ChoiceForTheMiddle_LaterStepNamesTheChosenMod()
    {
        var merged = Plan(Pin("B"), "A", "B", "C");

        TextOf(merged).Should().Be("b's text");
        merged.Notes.OfType<TextKept>().Should().Equal(
            new TextKept(1, 10, "B", "A", Chosen: true),
            new TextKept(1, 10, "B", "C", Chosen: true));
    }

    [Fact]
    public void ThreeMods_ChoiceForTheLowest_EarlierStepNoteIsChosenToo()
    {
        var merged = Plan(Pin("C"), "A", "B", "C");

        merged.Notes.OfType<TextKept>().Should().Equal(
            new TextKept(1, 10, "C", "B", Chosen: true),
            new TextKept(1, 10, "C", "A", Chosen: true));
    }

    [Fact]
    public void ChoiceForTheLeader_IsMarkedChosen()
    {
        var merged = Plan(Pin("A"), "A", "B", "C");

        TextOf(merged).Should().Be("a's text");
        merged.Notes.OfType<TextKept>().Should().OnlyContain(n => n.Chosen && n.KeptMod == "A");
        merged.StaleChoices.Should().BeEmpty();
    }

    [Fact]
    public void ChoiceForAModThatDoesNotEditTheRow_IsStale()
    {
        // D ships the file but no text under id 10, so it never contests the entry.
        var merged = Plan(Pin("D"), "A", "B", "D");

        TextOf(merged).Should().Be("a's text");
        merged.StaleChoices.Should().ContainSingle().Which.Should().Be((Key, $"D does not edit {Key}"));
        merged.Notes.OfType<TextKept>().Should().OnlyContain(n => !n.Chosen);
    }

    [Fact]
    public void StaleChoice_IsIgnored()
    {
        var plain = Plan(null, "A", "B", "D");
        var pinned = Plan(Pin("D"), "A", "B", "D");

        pinned.Bytes.Should().Equal(plain.Bytes);
        pinned.Notes.Should().Equal(plain.Notes);
    }

    [Fact]
    public void ChosenNote_RaisesNoLoss()
    {
        var merged = Plan(Pin("B"), "A", "B", "C");

        merged.Notes.OfType<TextKept>().Should().OnlyContain(n => n.Loss == null);
        Plan(null, "A", "B", "C").Notes.OfType<TextKept>().Should().OnlyContain(n => n.Loss != null, "an unpinned loss still raises its card");
    }

    [Fact]
    public void AStalePinReportedByTheResolverIsKept()
    {
        var pins = new PathChoices([], [(Key, "Gone no longer provides " + Path)]);

        Plan(pins, "A", "B").StaleChoices.Should().ContainSingle().Which.Reason.Should().Contain("Gone no longer provides");
    }

    [Fact]
    public void ThePlannerLeavesTheSuppliedPinsUntouched()
    {
        var pins = Pin("B");

        Plan(pins, "A", "B", "C");

        pins.Owner(Key).Should().BeNull("each plan records on a copy, so a second plan of the same pins starts clean");
    }
}
