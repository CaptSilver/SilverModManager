using System.Diagnostics;
using FluentAssertions;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk;

namespace NexusMods.Platform.Proton.Tests.Steam;

public class SteamLaunchOptionLineTests : IDisposable
{
    private const string Shim = "/home/user/.local/share/silvermod/bin/silvermod";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "launch-line-" + Guid.NewGuid().ToString("N"));

    public SteamLaunchOptionLineTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Test_ComposeBuildsSpan()
    {
        SteamLaunchOptionLine.Compose(Shim, "Skyrim - Survival")
            .Should().Be($"'{Shim}' run 'Skyrim - Survival' -- %command%");
    }

    [Fact]
    public void Test_ComposeEscapesQuoteInName()
    {
        SteamLaunchOptionLine.Compose(Shim, "Bob's loadout")
            .Should().Be($@"'{Shim}' run 'Bob'\''s loadout' -- %command%");
    }

    [Fact]
    public void Test_ComposeSplitsLiteralCommandToken()
    {
        var line = SteamLaunchOptionLine.Compose(Shim, "a %command% b");
        line.Should().Be($"'{Shim}' run 'a %comm''and% b' -- %command%");
        line.Split("%command%").Should().HaveCount(2, "only the real token may be left for Steam to replace");
    }

    [Fact]
    public void Test_ParseReadsBackComposedSpan()
    {
        var parsed = SteamLaunchOptionLine.Parse(SteamLaunchOptionLine.Compose(Shim, "Bob's %command% loadout"));
        parsed.HasSpan.Should().BeTrue();
        parsed.ShimPath.Should().Be(Shim);
        parsed.LoadoutName.Should().Be("Bob's %command% loadout");
        parsed.Prefix.Should().BeEmpty();
        parsed.Tail.Should().BeEmpty();
    }

    [Fact]
    public void Test_ParseSplitsPrefixSpanAndTail()
    {
        var parsed = SteamLaunchOptionLine.Parse($"MANGOHUD=1 gamemoderun '{Shim}' run 'X' -- %command% -windowed -novid");
        parsed.Prefix.Should().Be("MANGOHUD=1 gamemoderun");
        parsed.LoadoutName.Should().Be("X");
        parsed.Tail.Should().Be("-windowed -novid");
    }

    [Fact]
    public void Test_ParseRecognisesAnyShimDirectory()
    {
        var parsed = SteamLaunchOptionLine.Parse("'/opt/elsewhere/silvermod' run 'X' -- %command%");
        parsed.HasSpan.Should().BeTrue();
        parsed.ShimPath.Should().Be("/opt/elsewhere/silvermod");
    }

    [Fact]
    public void Test_ParseIgnoresOtherLaunchers()
    {
        var parsed = SteamLaunchOptionLine.Parse("'/opt/fluorine/fluorine-manager' run 'X' -- %command%");
        parsed.HasSpan.Should().BeFalse();
        parsed.Prefix.Should().Be("'/opt/fluorine/fluorine-manager' run 'X' --");
    }

    [Fact]
    public void Test_RewriteOnEmptyLineIsBareSpan()
    {
        var edit = SteamLaunchOptionLine.Rewrite("", Shim, "X");
        edit.IsRefused.Should().BeFalse();
        edit.Line.Should().Be($"'{Shim}' run 'X' -- %command%");
    }

    [Fact]
    public void Test_RewriteIsIdempotent()
    {
        var once = SteamLaunchOptionLine.Rewrite("PROTON_LOG=1 %command% -windowed", Shim, "X").Line!;
        once.Should().Be($"PROTON_LOG=1 '{Shim}' run 'X' -- %command% -windowed");
        SteamLaunchOptionLine.Rewrite(once, Shim, "X").Line.Should().Be(once);
    }

    [Fact]
    public void Test_RewriteForAnotherLoadoutReplacesOnlyTheSpan()
    {
        var first = SteamLaunchOptionLine.Rewrite("MANGOHUD=1 ~/lsfg %command% -windowed", Shim, "One").Line!;
        var second = SteamLaunchOptionLine.Rewrite(first, "/new/place/silvermod", "Two").Line;
        second.Should().Be("MANGOHUD=1 ~/lsfg '/new/place/silvermod' run 'Two' -- %command% -windowed");
    }

    [Fact]
    public void Test_RewriteWithoutCommandTokenMakesTheLineTheTail()
    {
        SteamLaunchOptionLine.Rewrite("-windowed -novid", Shim, "X").Line
            .Should().Be($"'{Shim}' run 'X' -- %command% -windowed -novid");
    }

    [Fact]
    public void Test_RewriteAcceptsWrapperCommands()
    {
        var edit = SteamLaunchOptionLine.Rewrite("~/fgmod/fgmod ~/lsfg %command%", Shim, "X");
        edit.IsRefused.Should().BeFalse();
        edit.Line.Should().Be($"~/fgmod/fgmod ~/lsfg '{Shim}' run 'X' -- %command%");
    }

    [Fact]
    public void Test_RewriteAcceptsQuotedHashInValue()
    {
        var edit = SteamLaunchOptionLine.Rewrite("FOO='a # b' BAR=\"c;d\" %command%", Shim, "X");
        edit.IsRefused.Should().BeFalse();
        edit.Line.Should().StartWith("FOO='a # b' BAR=\"c;d\" ");
    }

    [Fact]
    public void Test_RewriteAcceptsHashInTail()
    {
        SteamLaunchOptionLine.Rewrite("%command% # note", Shim, "X").IsRefused.Should().BeFalse();
    }

    [Theory]
    [InlineData("%command% >/tmp/game.log 2>&1")]
    [InlineData("%command% 2>&1 >|/tmp/game.log")]
    [InlineData("FOO=a#b %command%")]
    [InlineData("%command% # old; a && b | c")]
    public void Test_RewriteAcceptsWhatShDoesNotTreatAsOperators(string line)
    {
        var edit = SteamLaunchOptionLine.Rewrite(line, Shim, "X");
        edit.Refusal.Should().BeNull();
        edit.Line.Should().Be(line.Replace("%command%", $"'{Shim}' run 'X' -- %command%"));
    }

    [Theory]
    [InlineData("FOO=1 # %command%", "#")]
    [InlineData("a ; b %command%", ";")]
    [InlineData("a && b %command%", "&&")]
    [InlineData("a | b %command%", "|")]
    [InlineData("a & b %command%", "&")]
    [InlineData("echo `id` %command%", "`")]
    [InlineData("echo $(id) %command%", "$(")]
    public void Test_RewriteRefusesOperatorInPrefix(string line, string op)
    {
        var edit = SteamLaunchOptionLine.Rewrite(line, Shim, "X");
        edit.IsRefused.Should().BeTrue();
        edit.Line.Should().BeNull();
        edit.Refusal.Should().Contain($"'{op}'").And.Contain("before %command%");
    }

    [Theory]
    [InlineData("%command% ; echo hi", ";")]
    [InlineData("%command% && echo hi", "&&")]
    [InlineData("%command% | tee log", "|")]
    [InlineData("%command% >/tmp/game.log & echo hi", "&")]
    [InlineData("-windowed ; echo hi", ";")]
    public void Test_RewriteRefusesOperatorInTail(string line, string op)
    {
        var edit = SteamLaunchOptionLine.Rewrite(line, Shim, "X");
        edit.IsRefused.Should().BeTrue();
        edit.Refusal.Should().Contain($"'{op}'").And.Contain("after %command%");
    }

    [Fact]
    public void Test_RewriteRefusesFluorineLine()
    {
        const string fluorine =
            "'/home/bazzite/.local/share/fluorine/bin/fluorine-manager' -i 'Cyberpunk 2077 - NCR' -p 'Default' run --headless -e 'Cyberpunk 2077' # %command%";

        var edit = SteamLaunchOptionLine.Rewrite(fluorine, Shim, "X");
        edit.IsRefused.Should().BeTrue();
        edit.Refusal.Should().Contain("'#'");
    }

    [Fact]
    public void Test_RemoveDropsOnlyTheSpan()
    {
        var line = $"MANGOHUD=1 '{Shim}' run 'X' -- %command% -windowed";
        SteamLaunchOptionLine.Remove(line).Should().Be("MANGOHUD=1 %command% -windowed");
    }

    [Fact]
    public void Test_RemoveRestoresTheOriginalOfARewrittenLine()
    {
        foreach (var original in new[] { "", "-windowed", "%command%", "%command% -windowed" })
        {
            var rewritten = SteamLaunchOptionLine.Rewrite(original, Shim, "X").Line!;
            var removed = SteamLaunchOptionLine.Remove(rewritten);
            // Steam appends a line without %command% to the game command, so dropping a leading
            // "%command%" is the same set of options.
            removed.Should().Be(original.Replace("%command%", "").Trim());
        }
    }

    [Fact]
    public void Test_RemoveKeepsCommandTokenOfAPrefixedLine()
    {
        var rewritten = SteamLaunchOptionLine.Rewrite("FOO=1 %command% -x", Shim, "X").Line!;
        SteamLaunchOptionLine.Remove(rewritten).Should().Be("FOO=1 %command% -x");
    }

    [Fact]
    public void Test_RemoveLeavesLineWithoutSpanAlone()
    {
        SteamLaunchOptionLine.Remove("FOO=1 %command% -x").Should().Be("FOO=1 %command% -x");
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("Bob's loadout")]
    [InlineData("a %command% b")]
    [InlineData("%command%")]
    [InlineData("it's %command%'s")]
    [InlineData("spaces  and $dollar `tick` \"dq\" ; # &")]
    public void Test_ComposedLineRunsShimWithExactArguments(string name)
    {
        var binDir = Path.Combine(_tempDir, "bin");
        Directory.CreateDirectory(binDir);
        var shim = Path.Combine(binDir, "silvermod");
        File.WriteAllText(shim, "#!/bin/sh\nprintf '%s\\n' \"$@\"\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var line = SteamLaunchOptionLine.Compose(shim, name);
        // Steam substitutes the token textually before the shell sees the line.
        var substituted = line.Replace("%command%", "/games/launcher.exe");

        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true, WorkingDirectory = _tempDir };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(substituted);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        output.Should().Be($"run\n{name}\n--\n/games/launcher.exe\n");
    }

    [Fact]
    public void Test_WineDllOverridesStillFoundInComposedLine()
    {
        var line = SteamLaunchOptionLine.Rewrite("WINEDLLOVERRIDES=\"dxgi=n,b\" %command%", Shim, "X").Line!;
        WineParser.GetWineDllOverridesSection(line).ToString().Should().Be("dxgi=n,b");
    }
}
