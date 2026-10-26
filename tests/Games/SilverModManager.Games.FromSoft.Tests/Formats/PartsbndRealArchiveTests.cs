using System.Diagnostics;
using System.Text;
using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;

namespace SilverModManager.Games.FromSoft.Tests.Formats;

/// <summary>
/// Merges two real staged copies of one equipment part against the game's own copy. Never part of the default run:
/// it reads files that are not checked in, and writes nothing. Set these to run it, otherwise it is skipped:
/// <list type="bullet">
/// <item><c>SILVER_PARTSBND_A</c>: path of the first mod's partsbnd.dcx, the copy that wins a conflict.</item>
/// <item><c>SILVER_PARTSBND_B</c>: path of the second mod's copy of the same file.</item>
/// <item><c>SILVER_ER_GAME_FOLDER</c>: the Elden Ring Game folder; the vanilla copy is read from its Data*.bdt archives.</item>
/// <item><c>SILVER_PARTSBND_RELPATH</c>: optional, the file's game-relative path such as <c>parts/wp_a_0556.partsbnd.dcx</c>;
/// defaults to <c>parts/</c> plus the lowercased file name of A.</item>
/// </list>
/// A real bundle has the true member order, flags and a texture archive nobody built by hand, which the synthetic
/// fixtures only approximate. The merge, the texture round trip and the container header are checked, and the member
/// table and the timings go to the test output.
/// </summary>
public class PartsbndRealArchiveTests(ITestOutputHelper output)
{
    private const string AVariable = "SILVER_PARTSBND_A";
    private const string BVariable = "SILVER_PARTSBND_B";
    private const string GameVariable = "SILVER_ER_GAME_FOLDER";
    private const string RelPathVariable = "SILVER_PARTSBND_RELPATH";

    private const string ModA = "mod A";
    private const string ModB = "mod B";

    private static readonly FromSoftFormatTraits Traits = EldenRingGame.Definition.Formats;

    private sealed record Inputs(string PathA, string PathB, string GameFolder, string RelPath);

    private static Inputs RequireInputs()
    {
        var a = Environment.GetEnvironmentVariable(AVariable);
        var b = Environment.GetEnvironmentVariable(BVariable);
        var game = Environment.GetEnvironmentVariable(GameVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || string.IsNullOrEmpty(game),
            $"{AVariable}, {BVariable} and {GameVariable} are not all set");
        Assert.SkipUnless(File.Exists(a), $"{a} does not exist");
        Assert.SkipUnless(File.Exists(b), $"{b} does not exist");
        Assert.SkipUnless(Directory.Exists(game), $"{game} does not exist");

        var rel = Environment.GetEnvironmentVariable(RelPathVariable);
        if (string.IsNullOrEmpty(rel)) rel = "parts/" + Path.GetFileName(a!).ToLowerInvariant();
        return new Inputs(a!, b!, game!, rel);
    }

    private static (T Value, TimeSpan Elapsed) Timed<T>(Func<T> work)
    {
        var watch = Stopwatch.StartNew();
        var value = work();
        return (value, watch.Elapsed);
    }

    private static Dictionary<int, BinderEntry> Members(byte[] dcx)
        => Binders.For(Traits).Read(Dcx.Read(dcx, Traits)).ToDictionary(e => e.Id);

    private static string Source(byte[] merged, byte[] a, byte[] b, byte[] vanilla)
    {
        var fromA = merged.AsSpan().SequenceEqual(a);
        var fromB = merged.AsSpan().SequenceEqual(b);
        var fromVanilla = merged.AsSpan().SequenceEqual(vanilla);
        return (fromA, fromB, fromVanilla) switch
        {
            (true, true, true) => "all three agree",
            (true, true, false) => "A and B agree",
            (true, false, true) => "A (= vanilla)",
            (false, true, true) => "B (= vanilla)",
            (true, false, false) => ModA,
            (false, true, false) => ModB,
            (false, false, true) => "vanilla",
            _ => "merged from both",
        };
    }

    [Fact]
    public void TwoRealCopiesMergeAgainstTheGamesCopyAndEveryMemberComesFromAnInput()
    {
        var input = RequireInputs();
        var a = File.ReadAllBytes(input.PathA);
        var b = File.ReadAllBytes(input.PathB);
        var vanilla = GameArchives.ReadFile(input.GameFolder, input.RelPath, Traits);

        var notes = new List<IMergeNote>();
        var (merged, mergeTime) = Timed(() => BinderMemberMerge.ThreeWay(a, b, vanilla, Traits, notes, ModA, ModB));
        output.WriteLine($"merge: {mergeTime.TotalMilliseconds:F0} ms; {a.Length} B (A), {b.Length} B (B), {vanilla.Length} B (game), {merged.Length} B (merged)");

        var membersA = Members(a);
        var membersB = Members(b);
        var membersVanilla = Members(vanilla);
        var membersMerged = Members(merged);
        membersMerged.Keys.Should().BeEquivalentTo(membersVanilla.Keys, "a merge by id neither adds nor drops a member");

        var table = new StringBuilder("member provenance:\n");
        foreach (var id in membersVanilla.Keys.Order())
        {
            var source = Source(membersMerged[id].Data, membersA[id].Data, membersB[id].Data, membersVanilla[id].Data);
            table.AppendLine($"  {id,6}  {source,-18}  {membersVanilla[id].Name}");

            var changedA = !membersA[id].Data.AsSpan().SequenceEqual(membersVanilla[id].Data);
            var changedB = !membersB[id].Data.AsSpan().SequenceEqual(membersVanilla[id].Data);
            var texture = MemberPolicy.For(membersVanilla[id].Name) == MemberMerge.Texture;
            if (!changedA && !changedB) membersMerged[id].Data.Should().Equal(membersVanilla[id].Data, $"{membersVanilla[id].Name} was touched by nobody");
            else if (changedA && !changedB) membersMerged[id].Data.Should().Equal(membersA[id].Data, $"only A changed {membersVanilla[id].Name}");
            else if (!changedA) membersMerged[id].Data.Should().Equal(membersB[id].Data, $"only B changed {membersVanilla[id].Name}");
            else if (!texture) membersMerged[id].Data.Should().Equal(membersA[id].Data, $"both changed {membersVanilla[id].Name} and A wins an opaque member");
        }
        output.WriteLine(table.ToString());
        foreach (var note in notes) output.WriteLine("note: " + note.Describe());
    }

    [Fact]
    public void EveryRealTextureArchiveRewritesToTheBytesItWasReadFrom()
    {
        var input = RequireInputs();
        var a = File.ReadAllBytes(input.PathA);
        var b = File.ReadAllBytes(input.PathB);
        var vanilla = GameArchives.ReadFile(input.GameFolder, input.RelPath, Traits);
        var merged = BinderMemberMerge.ThreeWay(a, b, vanilla, Traits, null, ModA, ModB);

        var checkedCount = 0;
        foreach (var (label, dcx) in new[] { ("A", a), ("B", b), ("game", vanilla), ("merged", merged) })
        {
            foreach (var member in Members(dcx).Values.Where(m => MemberPolicy.For(m.Name) == MemberMerge.Texture))
            {
                Tpf.Write(Tpf.Read(member.Data)).Should().Equal(member.Data, $"{member.Name} in {label} must survive a read and write unchanged");
                checkedCount++;
            }
        }
        checkedCount.Should().BeGreaterThan(0, "a partsbnd with no texture archive is not the case this check exists for");
        output.WriteLine($"{checkedCount} texture archives round-tripped");
    }

    [Fact]
    public void TheWrittenContainerHeaderMatchesTheOneARealDfltFileCarries()
    {
        var input = RequireInputs();
        var dflt = new[] { input.PathA, input.PathB }
            .Select(p => (Path: p, Bytes: File.ReadAllBytes(p)))
            .Where(f => f.Bytes.Length >= Dcx.HeaderSize && f.Bytes.AsSpan(0x28, 4).SequenceEqual("DFLT"u8))
            .ToList();
        Assert.SkipWhen(dflt.Count == 0, "neither input is DFLT-compressed");

        foreach (var (path, bytes) in dflt)
        {
            var payload = Dcx.Read(bytes, Traits);
            var (written, writeTime) = Timed(() => Dcx.Write(payload, Traits.PackedWrite));
            output.WriteLine($"{Path.GetFileName(path)}: DFLT-{Traits.PackedWrite.Level} write {writeTime.TotalMilliseconds:F0} ms, {payload.Length} B -> {written.Length} B (original {bytes.Length} B)");

            written.AsSpan(0x04, 4).ToArray().Should().Equal(bytes.AsSpan(0x04, 4).ToArray(), $"the word at 0x04 of {path}");
            written[0x30].Should().Be(bytes[0x30], $"the compression level byte at 0x30 of {path}");
        }
    }
}
