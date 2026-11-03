using System.IO.Hashing;
using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Games.Bethesda.Loot.Database;
using SilverModManager.Games.Bethesda.Loot.Masterlists;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Conditions;

public class ConditionEvaluatorTests : IDisposable
{
    private readonly TempDirectory _data = new();
    private readonly FakeConditionContext _context;
    private readonly ConditionEvaluator _evaluator;

    public ConditionEvaluatorTests()
    {
        _context = new FakeConditionContext(_data.Path);
        _evaluator = new ConditionEvaluator(_context);
    }

    public void Dispose() => _data.Dispose();

    private bool Eval(string condition) => _evaluator.Evaluate(condition);

    private ConditionEvaluator For(LootGame game, out FakeConditionContext context)
    {
        context = new FakeConditionContext(_data.Path, game);
        return new ConditionEvaluator(context);
    }

    public class FilePaths : ConditionEvaluatorTests
    {
        [Fact]
        public void FilePath_FindsAFileByDifferentCase()
        {
            _data.Write("Mod.esp");

            Eval("file(\"MOD.ESP\")").Should().BeTrue();
        }

        [Fact]
        public void FilePath_FalseWhenMissing() => Eval("file(\"Mod.esp\")").Should().BeFalse();

        [Fact]
        public void FilePath_FindsAFileInASubfolderByDifferentCase()
        {
            _data.Write("SKSE/Plugins/a.dll");

            Eval("file(\"skse/plugins/A.DLL\")").Should().BeTrue();
        }

        [Fact]
        public void FilePath_FindsADirectory()
        {
            _data.CreateDirectory("Meshes");

            Eval("file(\"meshes\")").Should().BeTrue();
        }

        [Fact]
        public void FilePath_FindsAGhostedPlugin()
        {
            _data.Write("Mod.esp.ghost");

            Eval("file(\"Mod.esp\")").Should().BeTrue();
        }

        [Fact]
        public void FilePath_DoesNotGhostANonPlugin()
        {
            _data.Write("thing.dll.ghost");

            Eval("file(\"thing.dll\")").Should().BeFalse();
        }

        [Fact]
        public void FilePath_DoesNotGhostAnEslWhereTheGameHasNone()
        {
            _data.Write("Mod.esl.ghost");
            var oblivion = For(LootGame.Oblivion, out _);

            oblivion.Evaluate("file(\"Mod.esl\")").Should().BeFalse();
        }

        [Fact]
        public void FilePath_DoesNotGhostForOpenMW()
        {
            _data.Write("Mod.esp.ghost");
            var openMw = For(LootGame.OpenMW, out _);

            openMw.Evaluate("file(\"Mod.esp\")").Should().BeFalse();
        }

        [Fact]
        public void FileRegex_MatchesCaseInsensitively()
        {
            _data.Write("Cargo.toml");

            Eval("file(\"cargo.*\\.toml\")").Should().BeTrue();
        }

        [Fact]
        public void FileRegex_StripsGhostBeforeMatching()
        {
            _data.Write("Mod One.esp.ghost");

            Eval("file(\"Mod .*\\.esp\")").Should().BeTrue();
        }

        [Fact]
        public void FileRegex_DoesNotStripGhostOffANonPlugin()
        {
            _data.Write("thing.dll.ghost");

            Eval("file(\"thing\\.dll\")").Should().BeFalse();
        }

        [Fact]
        public void FileRegex_InASubfolder()
        {
            _data.Write("Textures/Foo1.dds");

            Eval("file(\"textures/Foo[0-9]\\.dds\")").Should().BeTrue();
            Eval("file(\"textures/Bar[0-9]\\.dds\")").Should().BeFalse();
        }

        [Fact]
        public void FileRegex_FalseWhenParentIsNotADirectory()
        {
            _data.Write("plain");

            Eval("file(\"plain/.*\")").Should().BeFalse();
        }

        [Fact]
        public void FileRegex_FalseWhenParentIsMissing() => Eval("file(\"missing/.*\\.esp\")").Should().BeFalse();

        [Fact]
        public void Many_NeedsTwoMatches()
        {
            _data.Write("Patch 1.esp");
            Eval("many(\"Patch .*\\.esp\")").Should().BeFalse();

            _data.Write("Patch 2.esp.ghost");
            // A new evaluator, because the first one remembers its answer for the rest of the sort.
            new ConditionEvaluator(_context).Evaluate("many(\"Patch .*\\.esp\")").Should().BeTrue();
        }

        [Fact]
        public void Many_CountsFoldersToo()
        {
            _data.CreateDirectory("Pack A");
            _data.CreateDirectory("Pack B");

            Eval("many(\"Pack .\")").Should().BeTrue();
        }

        [Fact]
        public void Many_IsFalseAcrossAMissingParent() => Eval("many(\"missing/.*\")").Should().BeFalse();

        [Fact]
        public void FileSize_EqualsTheLengthOfTheResolvedFile()
        {
            _data.Write("Thing.bin", new byte[1234]);

            Eval("file_size(\"thing.bin\", 1234)").Should().BeTrue();
            Eval("file_size(\"thing.bin\", 1235)").Should().BeFalse();
            Eval("file_size(\"missing.bin\", 0)").Should().BeFalse();
        }

        [Fact]
        public void FileSize_MatchesAFileTheUserCannotRead()
        {
            var file = _data.Write("Locked.bin", new byte[77]);
            System.IO.File.SetUnixFileMode(file, UnixFileMode.None);

            Eval("file_size(\"locked.bin\", 77)").Should().BeTrue();
            Eval("file_size(\"locked.bin\", 78)").Should().BeFalse();
        }
    }

    public class ActivePlugins : ConditionEvaluatorTests
    {
        [Fact]
        public void ActivePath_ComparesLowerCased()
        {
            _context.Active.Add("mod.esp");
            var evaluator = new ConditionEvaluator(_context);

            evaluator.Evaluate("active(\"MOD.ESP\")").Should().BeTrue();
            evaluator.Evaluate("active(\"other.esp\")").Should().BeFalse();
        }

        [Fact]
        public void ActivePath_AcceptsAnActiveSetThatKeepsItsCase()
        {
            _context.Active.Add("Mod.esp");
            var evaluator = new ConditionEvaluator(_context);

            evaluator.Evaluate("active(\"mod.esp\")").Should().BeTrue();
        }

        [Fact]
        public void ActivePath_DoesNotGhostOrLookAtTheDisk()
        {
            _data.Write("Mod.esp.ghost");

            Eval("active(\"Mod.esp\")").Should().BeFalse();
        }

        [Fact]
        public void ActiveRegex_MatchesAnActiveName()
        {
            _context.Active.Add("patch 3.esp");
            var evaluator = new ConditionEvaluator(_context);

            evaluator.Evaluate("active(\"Patch .*\\.esp\")").Should().BeTrue();
        }

        [Fact]
        public void ActiveRegex_IsAnchored()
        {
            _context.Active.Add("big patch 3.esp");
            var evaluator = new ConditionEvaluator(_context);

            evaluator.Evaluate("active(\"Patch .*\\.esp\")").Should().BeFalse();
        }

        [Fact]
        public void ActiveRegex_FalseWithNoActivePlugins() => Eval("active(\"Patch .*\\.esp\")").Should().BeFalse();

        [Fact]
        public void ManyActive_NeedsTwoMatches()
        {
            _context.Active.Add("a.esp");
            _context.Active.Add("b.esp");
            _context.Active.Add("c.esm");
            var evaluator = new ConditionEvaluator(_context);

            evaluator.Evaluate("many_active(\".*\\.esp\")").Should().BeTrue();
            evaluator.Evaluate("many_active(\".*\\.esm\")").Should().BeFalse();
        }
    }

    public class Masters : ConditionEvaluatorTests
    {
        [Fact]
        public void IsMaster_AsksTheContext()
        {
            _data.Write("Base.esm");
            _data.Write("Patch.esp");
            _context.Plugins["Base.esm"] = (true, null);
            _context.Plugins["Patch.esp"] = (false, null);

            Eval("is_master(\"Base.esm\")").Should().BeTrue();
            Eval("is_master(\"Patch.esp\")").Should().BeFalse();
            Eval("is_master(\"Unknown.esp\")").Should().BeFalse();
        }

        [Fact]
        public void IsMaster_FalseForAPluginTheContextKnowsButThatIsNotOnDisk()
        {
            _context.Plugins["Base.esm"] = (true, null);

            Eval("is_master(\"Base.esm\")").Should().BeFalse();
            Eval("file(\"Base.esm\")").Should().BeFalse();
        }

        [Fact]
        public void IsMaster_FalseForOpenMWWithoutAsking()
        {
            var openMw = For(LootGame.OpenMW, out var context);
            context.Plugins["Base.esm"] = (true, null);

            openMw.Evaluate("is_master(\"Base.esm\")").Should().BeFalse();
            context.IsMasterCalls.Should().Be(0);
        }
    }

    public class Checksums : ConditionEvaluatorTests
    {
        // CRC-32 of "123456789", the standard check value.
        private const string CheckValue = "CBF43926";

        [Fact]
        public void Checksum_MatchesCrc32OfTheBytes()
        {
            _data.Write("check.bin", "123456789");

            Eval($"checksum(\"check.bin\", {CheckValue})").Should().BeTrue();
            Eval("checksum(\"check.bin\", 00000001)").Should().BeFalse();
        }

        [Fact]
        public void Checksum_AgreesWithTheHashingLibraryOnLargerFiles()
        {
            var bytes = new byte[300_000];
            new Random(7).NextBytes(bytes);
            _data.Write("big.bin", bytes);
            var expected = Crc32.HashToUInt32(bytes);

            Eval($"checksum(\"big.bin\", {expected:X8})").Should().BeTrue();
        }

        [Fact]
        public void Checksum_FalseForMissingFile() => Eval($"checksum(\"missing.esp\", {CheckValue})").Should().BeFalse();

        [Fact]
        public void Checksum_FalseForADirectory()
        {
            _data.CreateDirectory("Meshes");

            Eval($"checksum(\"Meshes\", {CheckValue})").Should().BeFalse();
        }

        [Fact]
        public void Checksum_ReadsAGhostedPlugin()
        {
            _data.Write("Mod.esp.ghost", "123456789");

            Eval($"checksum(\"mod.esp\", {CheckValue})").Should().BeTrue();
        }

        [Fact]
        public void Checksum_ComparesTheKnownCrcAgainstEachLiteral()
        {
            _data.Write("check.bin", "123456789");

            Eval("checksum(\"check.bin\", 00000001)").Should().BeFalse();
            Eval($"checksum(\"check.bin\", {CheckValue})").Should().BeTrue();
        }
    }

    public class Versions : ConditionEvaluatorTests
    {
        [Fact]
        public void Version_FromPluginDescription()
        {
            _data.Write("Mod.esp");
            _context.Plugins["Mod.esp"] = (false, "Version: 2.1 of the mod");

            Eval("version(\"Mod.esp\", \"2.0\", >)").Should().BeTrue();
            Eval("version(\"Mod.esp\", \"2.1\", ==)").Should().BeTrue();
            Eval("version(\"Mod.esp\", \"2.2\", >=)").Should().BeFalse();
        }

        [Fact]
        public void Version_FalseForAPluginThatIsNotOnDisk()
        {
            _context.Plugins["Mod.esp"] = (false, "Version: 2.1");

            Eval("version(\"Mod.esp\", \"0\", >=)").Should().BeFalse();
        }

        [Fact]
        public void Version_FalseWhenNoDescriptionVersion()
        {
            _data.Write("Mod.esp");
            _context.Plugins["Mod.esp"] = (false, "No numbers here");

            Eval("version(\"Mod.esp\", \"0\", >=)").Should().BeFalse();
            Eval("version(\"Unknown.esp\", \"0\", >=)").Should().BeFalse();
        }

        [Fact]
        public void Version_OfAGhostedPluginUsesTheDescription()
        {
            _data.Write("Mod.esp.ghost");
            _context.Plugins["Mod.esp"] = (false, "v3.0");

            Eval("version(\"Mod.esp.ghost\", \"3.0\", ==)").Should().BeTrue();
        }

        [Fact]
        public void Version_FromPeFileVersion()
        {
            _data.Write("Game.exe", FakePe.WithVersion(new Version(1, 9, 32, 0)));

            Eval("version(\"game.exe\", \"1.9.32\", ==)").Should().BeTrue();
            Eval("version(\"game.exe\", \"1.9.33\", <)").Should().BeTrue();
            Eval("version(\"game.exe\", \"1.10\", >)").Should().BeFalse();
        }

        [Fact]
        public void Version_ResolvesBackslashesAndParentFolders()
        {
            _data.Write("Game.exe", FakePe.WithVersion(new Version(1, 8, 0, 0)));
            var dataFolder = _data.CreateDirectory("Data");
            var evaluator = new ConditionEvaluator(new FakeConditionContext(dataFolder));

            evaluator.Evaluate("version(\"..\\game.exe\", \"1.8\", ==)").Should().BeTrue();
        }

        [Fact]
        public void Version_FalseForNonPe()
        {
            _data.Write("notes.txt", "just some text");

            Eval("version(\"notes.txt\", \"0\", >=)").Should().BeFalse();
        }

        [Fact]
        public void Version_FalseForAMissingFile() => Eval("version(\"missing.exe\", \"0\", >=)").Should().BeFalse();

        [Fact]
        public void ProductVersion_FromStringTable()
        {
            _data.Write("Game.exe", FakePe.WithVersion(new Version(1, 0, 0, 0), "1, 6, 1170, 0"));

            Eval("product_version(\"game.exe\", \"1.6.1170\", ==)").Should().BeTrue();
            Eval("product_version(\"game.exe\", \"1.6.1171\", <)").Should().BeTrue();
        }

        [Fact]
        public void ProductVersion_FalseWithoutAStringTableOrAFile()
        {
            _data.Write("Game.exe", FakePe.WithVersion(new Version(1, 0, 0, 0)));

            Eval("product_version(\"game.exe\", \"0\", >=)").Should().BeFalse();
            Eval("product_version(\"missing.exe\", \"0\", >=)").Should().BeFalse();
        }

        [Fact]
        public void FilenameVersion_ComparesTheCapturedVersion()
        {
            _data.Write("Plugins/Tool 2.5.dll");
            _data.Write("Plugins/Tool 1.0.dll");

            Eval("filename_version(\"plugins/Tool (\\d+\\.\\d+)\\.dll\", \"2.0\", >)").Should().BeTrue();
            Eval("filename_version(\"plugins/Tool (\\d+\\.\\d+)\\.dll\", \"3.0\", >)").Should().BeFalse();
        }

        [Fact]
        public void FilenameVersion_StripsGhostFromPluginNames()
        {
            _data.Write("Mod 4.0.esp.ghost");

            Eval("filename_version(\"Mod (\\d+\\.\\d+)\\.esp\", \"4.0\", ==)").Should().BeTrue();
        }
    }

    public class Misc : ConditionEvaluatorTests
    {
        [Fact]
        public void Readable_TrueForAFileAndADirectory()
        {
            _data.Write("a.txt", "x");
            _data.CreateDirectory("Sub");

            Eval("readable(\"A.TXT\")").Should().BeTrue();
            Eval("readable(\"sub\")").Should().BeTrue();
        }

        [Fact]
        public void Readable_FalseWhenMissing() => Eval("readable(\"missing\")").Should().BeFalse();

        [Fact]
        public void Readable_FalseForAFileTheUserCannotRead()
        {
            Assert.SkipWhen(!OperatingSystem.IsLinux() || Environment.UserName == "root", "Needs permission bits that apply to this user");
            var file = _data.Write("secret.txt", "x");
            System.IO.File.SetUnixFileMode(file, UnixFileMode.None);

            try
            {
                Eval("readable(\"secret.txt\")").Should().BeFalse();
            }
            finally
            {
                System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        [Fact]
        public void IsExecutable_TrueForAPeImage()
        {
            _data.Write("Game.exe", FakePe.WithVersion(new Version(1, 0, 0, 0)));

            Eval("is_executable(\"game.exe\")").Should().BeTrue();
        }

        [Fact]
        public void IsExecutable_FalseForOtherFilesAndMissingOnes()
        {
            _data.Write("notes.txt", "text");

            Eval("is_executable(\"notes.txt\")").Should().BeFalse();
            Eval("is_executable(\"missing.exe\")").Should().BeFalse();
        }

        [Fact]
        public void DescriptionContains_SearchesTheDescription()
        {
            _data.Write("Mod.esp");
            _context.Plugins["Mod.esp"] = (false, "A mod for Skyrim. Requires SKSE.");

            Eval("description_contains(\"Mod.esp\", \"requires skse\")").Should().BeTrue();
            Eval("description_contains(\"Mod.esp\", \"^Requires\")").Should().BeFalse();
        }

        [Fact]
        public void DescriptionContains_FalseForAPluginTheContextKnowsButThatIsNotOnDisk()
        {
            _context.Plugins["Mod.esp"] = (false, "Requires SKSE.");

            Eval("description_contains(\"Mod.esp\", \"skse\")").Should().BeFalse();
        }

        [Fact]
        public void PeFunctions_TreatAReadErrorAsNoVersionAndNotExecutable()
        {
            // Opens fine and fails on the first read with EIO.
            System.IO.File.CreateSymbolicLink(System.IO.Path.Combine(_data.Path, "Broken.exe"), "/proc/self/mem");

            Eval("version(\"Broken.exe\", \"0\", >=)").Should().BeFalse();
            Eval("product_version(\"Broken.exe\", \"0\", >=)").Should().BeFalse();
            Eval("is_executable(\"Broken.exe\")").Should().BeFalse();
        }

        [Fact]
        public void DescriptionContains_FalseWithoutADescription()
        {
            Eval("description_contains(\"Unknown.esp\", \"x\")").Should().BeFalse();
        }
    }

    public class Combinators : ConditionEvaluatorTests
    {
        [Fact]
        public void Not_InvertsAFunctionAndAGroup()
        {
            _data.Write("a.esp");

            Eval("not file(\"a.esp\")").Should().BeFalse();
            Eval("not file(\"b.esp\")").Should().BeTrue();
            Eval("not (file(\"a.esp\") and file(\"b.esp\"))").Should().BeTrue();
        }

        [Fact]
        public void AndOr_FollowPrecedenceAndGrouping()
        {
            _data.Write("a.esp");

            Eval("file(\"a.esp\") or file(\"b.esp\") and file(\"c.esp\")").Should().BeTrue();
            Eval("(file(\"a.esp\") or file(\"b.esp\")) and file(\"c.esp\")").Should().BeFalse();
        }

        [Fact]
        public void And_StopsAtTheFirstFalseTerm()
        {
            Eval("file(\"missing.esp\") and is_master(\"Base.esm\")").Should().BeFalse();

            _context.IsMasterCalls.Should().Be(0);
        }

        [Fact]
        public void Or_StopsAtTheFirstTrueAlternative()
        {
            _data.Write("a.esp");

            Eval("file(\"a.esp\") or is_master(\"Base.esm\")").Should().BeTrue();

            _context.IsMasterCalls.Should().Be(0);
        }

        [Fact]
        public void Or_ReachesLaterAlternativesWhenEarlierOnesFail()
        {
            _data.Write("Base.esm");
            Eval("file(\"a.esp\") or is_master(\"Base.esm\")").Should().BeFalse();

            _context.IsMasterCalls.Should().Be(1);
        }
    }

    public class Failures : ConditionEvaluatorTests
    {
        [Fact]
        public void IoError_ThrowsConditionEvaluationException()
        {
            Assert.SkipWhen(!OperatingSystem.IsLinux() || Environment.UserName == "root", "Needs permission bits that apply to this user");
            var file = _data.Write("locked.esp", "x");
            System.IO.File.SetUnixFileMode(file, UnixFileMode.None);

            try
            {
                var act = () => Eval("checksum(\"locked.esp\", 00000001)");

                act.Should().Throw<ConditionEvaluationException>()
                    .Which.FileName.Should().Be("locked.esp");
            }
            finally
            {
                System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        [Fact]
        public void AConditionThatDoesNotParseThrowsTheParseException()
        {
            var act = () => Eval("file(");

            act.Should().Throw<ConditionParseException>();
        }

        [Fact]
        public void Result_IsCachedPerFunction()
        {
            var path = _data.Write("a.esp");
            Eval("file(\"a.esp\")").Should().BeTrue();

            System.IO.File.Delete(path);

            // The same function in another condition reuses the answer, so one sort sees one state of the disk.
            Eval("file(\"a.esp\") and file(\"a.esp\")").Should().BeTrue();
        }

        [Fact]
        public void ActiveResults_AreNotCachedBecauseTheyCostLessThanTheLookup()
        {
            // No observable difference, so only pin that repeated active() calls keep giving the right answer.
            _context.Active.Add("a.esp");
            var evaluator = new ConditionEvaluator(_context);

            evaluator.Evaluate("active(\"a.esp\")").Should().BeTrue();
            evaluator.Evaluate("active(\"a.esp\")").Should().BeTrue();
        }

        [Fact]
        public void ParsedConditionsAreReused()
        {
            _data.Write("a.esp");

            for (var i = 0; i < 3; i++) Eval("file(\"a.esp\") and not file(\"b.esp\")").Should().BeTrue();
        }
    }

    public class Containment : ConditionEvaluatorTests
    {
        private ConditionEvaluator InDataFolder(out string root, out string data)
        {
            root = _data.Path;
            data = _data.CreateDirectory("Data");
            return new ConditionEvaluator(new FakeConditionContext(data));
        }

        // A condition that would block forever on a pipe has to fail the test rather than hang it.
        private static bool EvalWithin(ConditionEvaluator evaluator, string condition)
        {
            var task = Task.Factory.StartNew(() => evaluator.Evaluate(condition), TaskCreationOptions.LongRunning);
            task.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("a pipe must not make the condition block");
            return task.Result;
        }

        [Fact]
        public void File_ReachesTheGameRootThroughAParentSegment()
        {
            var evaluator = InDataFolder(out _, out _);
            _data.Write("skse64_loader.exe");

            evaluator.Evaluate("file(\"../skse64_loader.exe\")").Should().BeTrue();
        }

        [Fact]
        public void Version_ReadsAFileInTheGameRoot()
        {
            var evaluator = InDataFolder(out _, out _);
            _data.Write("skse64_loader.exe", FakePe.WithVersion(new Version(2, 2, 6, 0)));

            evaluator.Evaluate("version(\"../skse64_loader.exe\", \"2.2.6\", ==)").Should().BeTrue();
        }

        // The game root is <scratch>/Game, so "../../secret" from Data lands on <scratch>/secret, a real file that
        // only the containment check keeps out of reach.
        [Theory]
        [InlineData("file(\"../../secret\")")]
        [InlineData("readable(\"../../secret\")")]
        [InlineData("checksum(\"../../secret\", {crc})")]
        [InlineData("file_size(\"../../secret\", {size})")]
        [InlineData("is_executable(\"../../secret\")")]
        [InlineData("version(\"../../secret\", \"1.2.3\", ==)")]
        [InlineData("file(\"/etc/passwd\")")]
        [InlineData("readable(\"/etc/passwd\")")]
        public void FunctionsRefuseAPathOutsideTheGameRoot(string condition)
        {
            var secret = FakePe.WithVersion(new Version(1, 2, 3, 0));
            _data.Write("secret", secret);
            var data = _data.CreateDirectory("Game/Data");
            var evaluator = new ConditionEvaluator(new FakeConditionContext(data));
            condition = condition
                .Replace("{crc}", System.IO.Hashing.Crc32.HashToUInt32(secret).ToString("X8"))
                .Replace("{size}", secret.Length.ToString());

            evaluator.Evaluate(condition).Should().BeFalse();
            evaluator.Evaluate("not " + condition).Should().BeTrue();
        }

        [Fact]
        public void FunctionsHoldForTheSecretFileWhenItIsInsideTheGameRoot()
        {
            // The same conditions as the refusal test, pointed at a file that is reachable, so the refusals above
            // are known to come from containment and not from wrong expected values.
            var secret = FakePe.WithVersion(new Version(1, 2, 3, 0));
            var data = _data.CreateDirectory("Game/Data");
            _data.Write("Game/secret", secret);
            var evaluator = new ConditionEvaluator(new FakeConditionContext(data));
            var crc = System.IO.Hashing.Crc32.HashToUInt32(secret);

            evaluator.Evaluate("file(\"../secret\")").Should().BeTrue();
            evaluator.Evaluate($"checksum(\"../secret\", {crc:X8})").Should().BeTrue();
            evaluator.Evaluate($"file_size(\"../secret\", {secret.Length})").Should().BeTrue();
            evaluator.Evaluate("is_executable(\"../secret\")").Should().BeTrue();
            evaluator.Evaluate("version(\"../secret\", \"1.2.3\", ==)").Should().BeTrue();
        }

        [Theory]
        [InlineData("checksum(\"pipe.esp\", 00000000)")]
        [InlineData("version(\"pipe.exe\", \"1\", ==)")]
        [InlineData("product_version(\"pipe.exe\", \"1\", ==)")]
        [InlineData("readable(\"pipe.exe\")")]
        [InlineData("is_executable(\"pipe.exe\")")]
        public void FunctionsTreatAPipeInTheDataFolderAsAbsent(string condition)
        {
            var evaluator = InDataFolder(out _, out _);
            _data.CreateFifo("Data/pipe.esp");
            _data.CreateFifo("Data/pipe.exe");

            EvalWithin(evaluator, condition).Should().BeFalse();
        }

        [Fact]
        public void FileSizeTreatsAPipeAsAbsent()
        {
            var evaluator = InDataFolder(out _, out _);
            _data.CreateFifo("Data/pipe.bin");

            EvalWithin(evaluator, "file_size(\"pipe.bin\", 0)").Should().BeFalse();
        }

        [Fact]
        public void ChecksumTreatsAPipeInTheGameRootAsAbsent()
        {
            var evaluator = InDataFolder(out _, out _);
            _data.CreateFifo("pipe.exe");

            EvalWithin(evaluator, "checksum(\"../pipe.exe\", 00000000)").Should().BeFalse();
        }

        [Fact]
        public void FunctionsFollowASymlinkIntoAStoreFolder()
        {
            var evaluator = InDataFolder(out _, out var data);
            using var store = new TempDirectory();
            var pe = FakePe.WithVersion(new Version(2, 2, 6, 0));
            System.IO.File.CreateSymbolicLink(Path.Combine(data, "link.exe"), store.Write("mod/real.exe", pe));
            var crc = System.IO.Hashing.Crc32.HashToUInt32(pe);

            evaluator.Evaluate($"checksum(\"link.exe\", {crc:X8})").Should().BeTrue();
            evaluator.Evaluate("version(\"link.exe\", \"2.2.6\", ==)").Should().BeTrue();
            evaluator.Evaluate($"file_size(\"link.exe\", {pe.Length})").Should().BeTrue();
            evaluator.Evaluate("readable(\"link.exe\")").Should().BeTrue();
            evaluator.Evaluate("is_executable(\"link.exe\")").Should().BeTrue();
        }

        [Theory]
        [InlineData("checksum(\"link.exe\", 00000000)")]
        [InlineData("version(\"link.exe\", \"1\", ==)")]
        [InlineData("readable(\"link.exe\")")]
        [InlineData("is_executable(\"link.exe\")")]
        [InlineData("file_size(\"link.exe\", 0)")]
        public void FunctionsTreatASymlinkToAPipeAsAbsent(string condition)
        {
            var evaluator = InDataFolder(out _, out var data);
            using var elsewhere = new TempDirectory();
            System.IO.File.CreateSymbolicLink(Path.Combine(data, "link.exe"), elsewhere.CreateFifo("pipe"));

            EvalWithin(evaluator, condition).Should().BeFalse();
        }

        [Theory]
        [InlineData("checksum(\"../x.exe\\0\", 00000000)")]
        [InlineData("file(\"../x.exe\\0\")")]
        [InlineData("readable(\"../x.exe\\0\")")]
        [InlineData("is_executable(\"../x.exe\\0\")")]
        [InlineData("version(\"../x.exe\\0\", \"1\", ==)")]
        [InlineData("file_size(\"../x.exe\\0\", 0)")]
        public void AConditionPathHoldingANulDoesNotHoldAndDoesNotThrow(string condition)
        {
            var evaluator = InDataFolder(out _, out _);
            _data.Write("x.exe");

            evaluator.Evaluate(condition.Replace("\\0", "\0")).Should().BeFalse();
        }

        [Fact]
        public void ActiveRefusesAPathEvenWhenTheContextListsIt()
        {
            _context.Active.Add("../x.esp");
            var evaluator = new ConditionEvaluator(_context);

            evaluator.Evaluate("active(\"../x.esp\")").Should().BeFalse();
        }

        [Fact]
        public void PluginLookupsRefuseAPathWithASeparator()
        {
            _data.Write("x.esp");
            var data = _data.CreateDirectory("Data");
            _data.Write("Data/sub/y.esp");
            var context = new FakeConditionContext(data);
            context.Plugins["../x.esp"] = (true, "Version: 1.0");
            context.Plugins["sub/y.esp"] = (true, "Version: 1.0");
            var evaluator = new ConditionEvaluator(context);

            evaluator.Evaluate("is_master(\"../x.esp\")").Should().BeFalse();
            evaluator.Evaluate("is_master(\"sub/y.esp\")").Should().BeFalse();
            evaluator.Evaluate("version(\"../x.esp\", \"1.0\", ==)").Should().BeFalse();
            evaluator.Evaluate("description_contains(\"../x.esp\", \"Version\")").Should().BeFalse();
            context.IsMasterCalls.Should().Be(0);
        }
    }
}
