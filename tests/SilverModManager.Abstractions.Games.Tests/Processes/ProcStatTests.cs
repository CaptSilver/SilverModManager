using FluentAssertions;
using SilverModManager.Abstractions.Games.Processes;

namespace SilverModManager.Abstractions.Games.Tests.Processes;

public class ProcStatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "silvermod-procstat-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Parse_CommWithSpacesAndParentheses_ReadsStateAndParentAfterTheLastParen()
    {
        ProcStat.Parse("1234 (my (odd) game) S 42 1 1 0").Should().Be(new ProcStat('S', 42));
    }

    [Fact]
    public void Parse_NoClosingParen_IsNull()
    {
        ProcStat.Parse("1234 no-parens S 42").Should().BeNull();
    }

    [Fact]
    public void Parse_StateOnly_HasNoParent()
    {
        ProcStat.Parse("1 (x) R").Should().Be(new ProcStat('R', null));
    }

    [Fact]
    public void Parse_NothingAfterTheName_IsNull()
    {
        ProcStat.Parse("1 (x)").Should().BeNull();
        ProcStat.Parse("1 (x) ").Should().BeNull();
    }

    [Theory]
    [InlineData('R', true)]
    [InlineData('S', true)]
    [InlineData('D', true)]
    [InlineData('T', true)]
    [InlineData('Z', false)]
    [InlineData('X', false)]
    public void Parse_State_IsAliveUnlessZombieOrDead(char state, bool alive)
    {
        ProcStat.Parse($"9 (p) {state} 1").Should().NotBeNull().And.Match<ProcStat?>(s => s!.Value.IsAlive == alive);
    }

    [Fact]
    public void Read_FromAFixtureRoot_ReadsThatRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "77"));
        File.WriteAllText(Path.Combine(_root, "77", "stat"), "77 (fixture) Z 5 77 77 0");

        ProcStat.Read(_root, 77).Should().Be(new ProcStat('Z', 5));
    }

    [Fact]
    public void Read_AMissingPid_IsNull()
    {
        Directory.CreateDirectory(_root);

        ProcStat.Read(_root, 12345).Should().BeNull();
    }

    [Fact]
    public void Read_OwnProcess_HasAPositiveParent()
    {
        var stat = ProcStat.Read("/proc", Environment.ProcessId);

        stat.Should().NotBeNull();
        stat!.Value.ParentPid.Should().BePositive();
    }
}
