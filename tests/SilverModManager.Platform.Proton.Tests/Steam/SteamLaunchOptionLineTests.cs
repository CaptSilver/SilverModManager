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
        SteamLaunchOptionLine.Compose(Shim, "Skyrim - Survival", [], [])
            .Should().Be($"'{Shim}' run 'Skyrim - Survival' -- %command%");
    }

    [Theory]
    [InlineData("TOKEN=abc PROTON_LOG=1 %command%", "TOKEN=... PROTON_LOG=... %command%")]
    [InlineData("SECRET=\"two words\" MORE='it'\\''s' %command%", "SECRET=... MORE=... %command%")]
    [InlineData("env X=1 %command% Y=2 --flag=3", "env X=... %command% Y=2 --flag=3")]
    [InlineData("A=1 B=2 run-something C=3", "A=... B=... run-something C=3")]
    [InlineData("PROTON_LOG=1 '/x/bin/silvermod' run 'K=v' -- %command%", "PROTON_LOG=... '/x/bin/silvermod' run 'K=v' -- %command%")]
    [InlineData("%command% --windowed", "%command% --windowed")]
    [InlineData("--fullscreen -novid", "--fullscreen -novid")]
    [InlineData("", "")]
    public void Test_RedactEnvPrefixValuesKeepsKeysAndTheRestOfTheLine(string line, string expected)
    {
        SteamLaunchOptionLine.RedactEnvPrefixValues(line).Should().Be(expected);
    }

    [Fact]
    public void Test_ComposeEscapesQuoteInName()
    {
        SteamLaunchOptionLine.Compose(Shim, "Bob's loadout", [], [])
            .Should().Be($@"'{Shim}' run 'Bob'\''s loadout' -- %command%");
    }

    [Fact]
    public void Test_ComposeSplitsLiteralCommandToken()
    {
        var line = SteamLaunchOptionLine.Compose(Shim, "a %command% b", [], []);
        line.Should().Be($"'{Shim}' run 'a %comm''and% b' -- %command%");
        line.Split("%command%").Should().HaveCount(2, "only the real token may be left for Steam to replace");
    }

    [Fact]
    public void Test_ParseReadsBackComposedSpan()
    {
        var parsed = SteamLaunchOptionLine.Parse(SteamLaunchOptionLine.Compose(Shim, "Bob's %command% loadout", [], []));
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
        var edit = SteamLaunchOptionLine.Rewrite("", Shim, "X", [], []);
        edit.IsRefused.Should().BeFalse();
        edit.Line.Should().Be($"'{Shim}' run 'X' -- %command%");
    }

    [Fact]
    public void Test_RewriteIsIdempotent()
    {
        var once = SteamLaunchOptionLine.Rewrite("PROTON_LOG=1 %command% -windowed", Shim, "X", [], []).Line!;
        once.Should().Be($"PROTON_LOG=1 '{Shim}' run 'X' -- %command% -windowed");
        SteamLaunchOptionLine.Rewrite(once, Shim, "X", [], []).Line.Should().Be(once);
    }

    [Fact]
    public void Test_RewriteForAnotherLoadoutReplacesOnlyTheSpan()
    {
        var first = SteamLaunchOptionLine.Rewrite("MANGOHUD=1 ~/lsfg %command% -windowed", Shim, "One", [], []).Line!;
        var second = SteamLaunchOptionLine.Rewrite(first, "/new/place/silvermod", "Two", [], []).Line;
        second.Should().Be("MANGOHUD=1 ~/lsfg '/new/place/silvermod' run 'Two' -- %command% -windowed");
    }

    [Fact]
    public void Test_RewriteWithoutCommandTokenMakesTheLineTheTail()
    {
        SteamLaunchOptionLine.Rewrite("-windowed -novid", Shim, "X", [], []).Line
            .Should().Be($"'{Shim}' run 'X' -- %command% -windowed -novid");
    }

    [Fact]
    public void Test_RewriteAcceptsWrapperCommands()
    {
        var edit = SteamLaunchOptionLine.Rewrite("~/fgmod/fgmod ~/lsfg %command%", Shim, "X", [], []);
        edit.IsRefused.Should().BeFalse();
        edit.Line.Should().Be($"~/fgmod/fgmod ~/lsfg '{Shim}' run 'X' -- %command%");
    }

    [Fact]
    public void Test_RewriteAcceptsQuotedHashInValue()
    {
        var edit = SteamLaunchOptionLine.Rewrite("FOO='a # b' BAR=\"c;d\" %command%", Shim, "X", [], []);
        edit.IsRefused.Should().BeFalse();
        edit.Line.Should().StartWith("FOO='a # b' BAR=\"c;d\" ");
    }

    [Fact]
    public void Test_RewriteAcceptsHashInTail()
    {
        SteamLaunchOptionLine.Rewrite("%command% # note", Shim, "X", [], []).IsRefused.Should().BeFalse();
    }

    [Theory]
    [InlineData("%command% >/tmp/game.log 2>&1")]
    [InlineData("%command% 2>&1 >|/tmp/game.log")]
    [InlineData("FOO=a#b %command%")]
    [InlineData("%command% # old; a && b | c")]
    public void Test_RewriteAcceptsWhatShDoesNotTreatAsOperators(string line)
    {
        var edit = SteamLaunchOptionLine.Rewrite(line, Shim, "X", [], []);
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
        var edit = SteamLaunchOptionLine.Rewrite(line, Shim, "X", [], []);
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
        var edit = SteamLaunchOptionLine.Rewrite(line, Shim, "X", [], []);
        edit.IsRefused.Should().BeTrue();
        edit.Refusal.Should().Contain($"'{op}'").And.Contain("after %command%");
    }

    [Fact]
    public void Test_RewriteRefusesFluorineLine()
    {
        const string fluorine =
            "'/home/bazzite/.local/share/fluorine/bin/fluorine-manager' -i 'Cyberpunk 2077 - NCR' -p 'Default' run --headless -e 'Cyberpunk 2077' # %command%";

        var edit = SteamLaunchOptionLine.Rewrite(fluorine, Shim, "X", [], []);
        edit.IsRefused.Should().BeTrue();
        edit.Refusal.Should().Contain("'#'");
    }

    private const string RealFluorineLine =
        "'/home/bazzite/.local/share/fluorine/bin/fluorine-manager' -i 'Cyberpunk 2077 - NCR' -p 'Default' run --headless -e 'Cyberpunk 2077' # %command%";

    [Fact]
    public void Test_FindOtherManagerRecognisesFluorine()
    {
        var manager = SteamLaunchOptionLine.FindOtherManager(RealFluorineLine);

        manager.Should().NotBeNull();
        manager!.Name.Should().Be("Fluorine");
        manager.Command.Should().EndWith("/fluorine-manager");
    }

    [Fact]
    public void Test_FindOtherManagerCallsAnUnknownCommandBeforeAHashUnrecognised()
    {
        var manager = SteamLaunchOptionLine.FindOtherManager("'/opt/x/launcher' --foo # %command%");

        manager.Should().NotBeNull();
        manager!.Name.Should().BeNull();
        manager.Command.Should().Be("/opt/x/launcher");
    }

    [Theory]
    [InlineData("")]
    [InlineData("%command%")]
    [InlineData("MANGOHUD=1 %command%")]
    [InlineData("~/lsfg %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dsound=n,b\" SteamDeck=0 ~/fgmod/fgmod ~/lsfg %command%")]
    [InlineData("'/home/user/.local/share/silvermod/bin/silvermod' run 'X' -- %command%")]
    [InlineData("%command% # note")]
    [InlineData("FOO='a # b' %command%")]
    [InlineData("'/opt/x/tool' 'arg # 1' %command%")]
    [InlineData("FOO=a#b %command%")]
    [InlineData("'/opt/x/tool' -e 'a#b' %command%")]
    [InlineData("FOO=1 # %command%")]
    [InlineData("# '/x/fluorine-manager' %command%")]
    [InlineData("'/x/fluorine-manager' -i 'G'")]
    public void Test_FindOtherManagerIsNullForOrdinaryLines(string line)
    {
        SteamLaunchOptionLine.FindOtherManager(line).Should().BeNull();
    }

    [Fact]
    public void Test_ReplaceKeepsTheEnvAssignmentsAndDropsTheManager()
    {
        var edit = SteamLaunchOptionLine.Replace("MANGOHUD=1 " + RealFluorineLine, Shim, "X", Required, []);

        edit.Refusal.Should().BeNull();
        edit.Line.Should().Be($"MANGOHUD=1 WINEDLLOVERRIDES=\"{RequiredValue}\" '{Shim}' run 'X' -- %command%");
    }

    [Fact]
    public void Test_ReplaceOfTheRealFluorineLineIsTheBareSpan()
    {
        var edit = SteamLaunchOptionLine.Replace(RealFluorineLine, Shim, "X", [], []);

        edit.Refusal.Should().BeNull();
        edit.Line.Should().Be(SteamLaunchOptionLine.Compose(Shim, "X", [], []));
    }

    [Fact]
    public void Test_ReplaceDropsACommentedTailAndKeepsALiveOne()
    {
        SteamLaunchOptionLine.Replace(RealFluorineLine + " -windowed", Shim, "X", [], []).Line
            .Should().Be($"'{Shim}' run 'X' -- %command%");

        SteamLaunchOptionLine.Replace("'/x/fluorine-manager' run %command% -windowed", Shim, "X", [], []).Line
            .Should().Be($"'{Shim}' run 'X' -- %command% -windowed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("MANGOHUD=1 ~/lsfg %command%")]
    [InlineData("'/x/fluorine-manager' -i 'G'")]
    public void Test_ReplaceRefusesALineWithNoOtherManager(string line)
    {
        var edit = SteamLaunchOptionLine.Replace(line, Shim, "X", [], []);

        edit.Line.Should().BeNull();
        edit.Refusal.Should().Be("nothing of another manager to replace; the ordinary write applies");
    }

    [Fact]
    public void Test_ReplaceRefusesAnOperatorInAKeptTail()
    {
        var edit = SteamLaunchOptionLine.Replace("'/x/fluorine-manager' run %command% ; echo hi", Shim, "X", [], []);

        edit.Line.Should().BeNull();
        edit.Refusal.Should().Contain("';'").And.Contain("after %command%");
    }

    [Fact]
    public void Test_RewriteRefusesAKnownManagerUsedAsAWrapper()
    {
        var edit = SteamLaunchOptionLine.Rewrite("'/x/fluorine-manager' run %command%", Shim, "X", [], []);

        edit.Line.Should().BeNull();
        edit.Refusal.Should().Be("The launch options start Fluorine (/x/fluorine-manager), which manages the game itself. " +
                                 "Two managers cannot both run it; replace the line to use this loadout.");
    }

    [Fact]
    public void Test_RefusalOfAnotherManagersLineNamesTheReplacePath()
    {
        var edit = SteamLaunchOptionLine.Rewrite(RealFluorineLine, Shim, "X", [], []);

        edit.Refusal.Should().Contain("'#'").And.Contain("Fluorine").And.Contain("--replace-other-manager");
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
            var rewritten = SteamLaunchOptionLine.Rewrite(original, Shim, "X", [], []).Line!;
            var removed = SteamLaunchOptionLine.Remove(rewritten);
            // Steam appends a line without %command% to the game command, so dropping a leading
            // "%command%" is the same set of options.
            removed.Should().Be(original.Replace("%command%", "").Trim());
        }
    }

    [Fact]
    public void Test_RemoveKeepsCommandTokenOfAPrefixedLine()
    {
        var rewritten = SteamLaunchOptionLine.Rewrite("FOO=1 %command% -x", Shim, "X", [], []).Line!;
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

        var line = SteamLaunchOptionLine.Compose(shim, name, [], []);
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

    private static readonly string[] SkipLauncher = ["--launcher-skip"];

    [Fact]
    public void Test_ComposeKeepsLauncherSkipArgumentsAfterCommand()
    {
        SteamLaunchOptionLine.Compose(Shim, "X", [], SkipLauncher)
            .Should().Be($"'{Shim}' run 'X' -- %command% --launcher-skip");
    }

    [Fact]
    public void Test_RewriteAddsLauncherSkipAfterTheUsersTail()
    {
        var edit = SteamLaunchOptionLine.Rewrite("MANGOHUD=1 %command% -windowed", Shim, "X", [], SkipLauncher);

        edit.Line.Should().Be($"MANGOHUD=1 '{Shim}' run 'X' -- %command% -windowed --launcher-skip");
    }

    [Theory]
    [InlineData("%command%")]
    [InlineData("%command% --launcher-skip")]
    [InlineData("%command% -windowed --launcher-skip")]
    [InlineData("--launcher-skip")]
    public void Test_RewriteIsIdempotentWithLauncherSkip(string existing)
    {
        var first = SteamLaunchOptionLine.Rewrite(existing, Shim, "X", Required, SkipLauncher).Line!;
        var second = SteamLaunchOptionLine.Rewrite(first, Shim, "X", Required, SkipLauncher).Line!;

        second.Should().Be(first);
        first.Split(' ').Count(w => w == "--launcher-skip").Should().Be(1);
    }

    [Fact]
    public void Test_RewritePutsLauncherSkipBeforeACommentInTheTail()
    {
        var first = SteamLaunchOptionLine.Rewrite("%command% -windowed # my note", Shim, "X", [], SkipLauncher).Line!;
        var second = SteamLaunchOptionLine.Rewrite(first, Shim, "X", [], SkipLauncher).Line!;

        first.Should().Be($"'{Shim}' run 'X' -- %command% -windowed --launcher-skip # my note");
        second.Should().Be(first);
    }

    [Fact]
    public void Test_RewriteDoesNotCountALauncherSkipInsideAComment()
    {
        SteamLaunchOptionLine.Rewrite("%command% # --launcher-skip", Shim, "X", [], SkipLauncher).Line
            .Should().Be($"'{Shim}' run 'X' -- %command% --launcher-skip # --launcher-skip");
    }

    [Fact]
    public void Test_RewriteWithoutLauncherSkipLeavesTheTailAlone()
    {
        SteamLaunchOptionLine.Rewrite("%command% -windowed", Shim, "X", [], []).Line
            .Should().Be($"'{Shim}' run 'X' -- %command% -windowed");
    }

    [Fact]
    public void Test_ReplaceKeepsLauncherSkipAfterCommand()
    {
        var edit = SteamLaunchOptionLine.Replace(RealFluorineLine, Shim, "X", [], SkipLauncher);

        edit.IsRefused.Should().BeFalse(edit.Refusal);
        edit.Line.Should().EndWith("-- %command% --launcher-skip");
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("$HOME")]
    [InlineData("%command%")]
    [InlineData("")]
    public void Test_ComposeRefusesLauncherSkipArgumentsThatAreNotPlainWords(string argument)
    {
        var act = () => SteamLaunchOptionLine.Compose(Shim, "X", [], [argument]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Test_WineDllOverridesStillFoundInComposedLine()
    {
        var line = SteamLaunchOptionLine.Rewrite("WINEDLLOVERRIDES=\"dxgi=n,b\" %command%", Shim, "X", [], []).Line!;
        WineParser.GetWineDllOverridesSection(line).ToString().Should().Be("dxgi=n,b");
    }

    private static readonly IReadOnlyList<WineDllOverride> Required =
    [
        new("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
        new("version", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
    ];

    private const string RequiredValue = "winmm=n,b;version=n,b";

    [Fact]
    public void Test_ComposeWithOverridesPutsTheAssignmentBeforeTheSpan()
    {
        SteamLaunchOptionLine.Compose(Shim, "X", Required, [])
            .Should().Be($"WINEDLLOVERRIDES=\"{RequiredValue}\" '{Shim}' run 'X' -- %command%");
    }

    [Fact]
    public void Test_RewriteMergesIntoAnExistingWineDllOverridesAssignment()
    {
        var edit = SteamLaunchOptionLine.Rewrite("WINEDLLOVERRIDES=\"dxgi=n,b;winmm=b\" MANGOHUD=1 ~/lsfg %command%", Shim, "X", Required, []);

        edit.Refusal.Should().BeNull();
        edit.Line.Should().Be($"WINEDLLOVERRIDES=\"dxgi=n,b;winmm=n,b;version=n,b\" MANGOHUD=1 ~/lsfg '{Shim}' run 'X' -- %command%");
    }

    [Fact]
    public void Test_RewriteAddsTheAssignmentAfterTheLeadingAssignmentsNotAfterAWrapper()
    {
        var edit = SteamLaunchOptionLine.Rewrite("MANGOHUD=1 ~/lsfg %command%", Shim, "X", Required, []);

        edit.Line.Should().Be($"MANGOHUD=1 WINEDLLOVERRIDES=\"{RequiredValue}\" ~/lsfg '{Shim}' run 'X' -- %command%");
    }

    [Theory]
    [InlineData("")]
    [InlineData("MANGOHUD=1 ~/lsfg %command% -windowed")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=n\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"winmm=n,b;version=n,b\" %command%")]
    [InlineData("env WINEDLLOVERRIDES=dxgi=n %command%")]
    public void Test_RewriteWithOverridesIsIdempotent(string original)
    {
        var once = SteamLaunchOptionLine.Rewrite(original, Shim, "X", Required, []).Line!;

        SteamLaunchOptionLine.Rewrite(once, Shim, "X", Required, []).Line.Should().Be(once);
    }

    [Fact]
    public void Test_RewriteMergesInPlaceIntoAnAssignmentAfterEnv()
    {
        var edit = SteamLaunchOptionLine.Rewrite("env WINEDLLOVERRIDES=dxgi=n %command%", Shim, "X", Required, []);

        edit.Line.Should().Be($"env WINEDLLOVERRIDES=\"dxgi=n;{RequiredValue}\" '{Shim}' run 'X' -- %command%");
    }

    [Fact]
    public void Test_RewriteMergesIntoEveryWineDllOverridesWordBeforeTheSpan()
    {
        var edit = SteamLaunchOptionLine.Rewrite("WINEDLLOVERRIDES=a=n env WINEDLLOVERRIDES=b=n %command%", Shim, "X", Required, []);

        edit.Line.Should().Be($"WINEDLLOVERRIDES=\"a=n;{RequiredValue}\" env WINEDLLOVERRIDES=\"b=n;{RequiredValue}\" '{Shim}' run 'X' -- %command%");
    }

    [Fact]
    public void Test_RewriteLeavesAnAlreadyCompleteAssignmentByteForByte()
    {
        var edit = SteamLaunchOptionLine.Rewrite($"WINEDLLOVERRIDES='{RequiredValue}' %command%", Shim, "X", Required, []);

        edit.Line.Should().Be($"WINEDLLOVERRIDES='{RequiredValue}' '{Shim}' run 'X' -- %command%");
    }

    [Theory]
    [InlineData("dxgi=n,b;path=$HOME")]
    [InlineData("dxgi=n,b;path=`id`")]
    [InlineData("dxgi=n,b;path=a\\b")]
    [InlineData("dxgi=n,b;path=a b")]
    public void Test_RewriteRefusesAWineDllOverridesValueItCannotDoubleQuote(string value)
    {
        var edit = SteamLaunchOptionLine.Rewrite($"WINEDLLOVERRIDES='{value}' %command%", Shim, "X", Required, []);

        edit.IsRefused.Should().BeTrue();
        edit.Line.Should().BeNull();
        edit.Refusal.Should().Contain(value);
    }

    [Fact]
    public void Test_ComposedLineWithOverridesReachesTheShimThroughAWrapper()
    {
        var binDir = Path.Combine(_tempDir, "bin");
        Directory.CreateDirectory(binDir);
        var shim = Path.Combine(binDir, "silvermod");
        File.WriteAllText(shim, "#!/bin/sh\nprintf '%s' \"$WINEDLLOVERRIDES\"\n");
        var wrapper = Path.Combine(binDir, "lsfg");
        File.WriteAllText(wrapper, "#!/bin/sh\nexec \"$@\"\n");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(shim, mode);
        File.SetUnixFileMode(wrapper, mode);

        var line = SteamLaunchOptionLine.Rewrite($"WINEDLLOVERRIDES=dxgi=n,b '{wrapper}' %command%", shim, "X", Required, []).Line!;
        var substituted = line.Replace("%command%", "/games/launcher.exe");

        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true, WorkingDirectory = _tempDir };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(substituted);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        output.Should().Be($"dxgi=n,b;{RequiredValue}");
    }

    [Fact]
    public void Test_OverridesInTheRewrittenLineParseBackToTheRequiredEntries()
    {
        var line = SteamLaunchOptionLine.Rewrite("WINEDLLOVERRIDES=\"dxgi=n,b\" %command%", Shim, "X", Required, []).Line!;

        var parsed = WineParser.ParseEnvironmentVariable(WineParser.GetWineDllOverridesSection(line));

        parsed.Select(o => o.DllName).Should().Equal("dxgi", "winmm", "version");
        parsed.Skip(1).Select(o => o.ToString()).Should().Equal(Required.Select(o => o.ToString()));
    }

    private const string RealMe3Line =
        "'/home/u/.local/bin/me3' launch -p '/home/u/build/elden_ring_mods/tools/me3/erm-coop.me3' # %command%";

    private const string RealMe3LineWithOverrides =
        "WINEDLLOVERRIDES=\"d3dcompiler_47=n;dxgi=n,b\" " + RealMe3Line;

    [Theory]
    [InlineData(RealMe3Line)]
    [InlineData(RealMe3LineWithOverrides)]
    public void Test_FindOtherManagerRecognisesMe3(string line)
    {
        var manager = SteamLaunchOptionLine.FindOtherManager(line);

        manager.Should().NotBeNull();
        manager!.Name.Should().Be("me3");
        manager.Command.Should().Be("/home/u/.local/bin/me3");
    }

    [Fact]
    public void Test_ReplaceOfMe3sLineIsTheBareSpanWithTheCommentedTailDropped()
    {
        var edit = SteamLaunchOptionLine.Replace(RealMe3Line, Shim, "X", [], []);

        edit.Refusal.Should().BeNull();
        edit.Line.Should().Be($"'{Shim}' run 'X' -- %command%");
    }

    [Fact]
    public void Test_ReplaceOfMe3sLineWithAnOverridesPrefixKeepsTheAssignment()
    {
        var edit = SteamLaunchOptionLine.Replace(RealMe3LineWithOverrides, Shim, "X", [], []);

        edit.Refusal.Should().BeNull();
        edit.Line.Should().Be($"WINEDLLOVERRIDES=\"d3dcompiler_47=n;dxgi=n,b\" '{Shim}' run 'X' -- %command%");
    }
}
