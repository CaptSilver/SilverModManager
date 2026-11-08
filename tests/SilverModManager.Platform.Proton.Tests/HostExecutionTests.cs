using System.Runtime.InteropServices;
using FluentAssertions;

namespace SilverModManager.Platform.Proton.Tests;

/// <summary>
/// The probe reads a folder of binfmt entries; each test writes the entries as the kernel prints them.
/// </summary>
public class HostExecutionTests : IDisposable
{
    private const string X86_64Magic = "7f454c4602010100000000000000000002003e00";
    private const string Aarch64Magic = "7f454c460201010000000000000000000200b700";
    private const string Mask = "ffffffffffffff00fffffffffffffffffeffffff";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "smm-binfmt-" + Guid.NewGuid().ToString("N"));

    public HostExecutionTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void Entry(string name, string state, string magic, string mask, int offset = 0)
        => File.WriteAllText(Path.Combine(_directory, name), $"{state}\ninterpreter /usr/bin/{name}\nflags: F\noffset {offset}\nmagic {magic}\nmask {mask}\n");

    private X86_64Support Probe(Architecture architecture = Architecture.Arm64) => new HostExecution(_directory, architecture).CanRunX86_64();

    [Fact]
    public void AnX64ProcessRunsX86_64Natively()
    {
        new HostExecution("/nonexistent/binfmt_misc", Architecture.X64).CanRunX86_64().Should().Be(X86_64Support.Native);
    }

    [Fact]
    public void AnEnabledEntryWithTheX86_64MachineCodeMeansAnInterpreter()
    {
        Entry("FEX-x86_64", "enabled", X86_64Magic, Mask);

        Probe().Should().Be(X86_64Support.Interpreter);
    }

    [Fact]
    public void TheHostsQemuAarch64EntryIsNotAnX86_64Interpreter()
    {
        Entry("qemu-aarch64", "enabled", Aarch64Magic, Mask);

        Probe().Should().Be(X86_64Support.None);
    }

    [Fact]
    public void ADisabledEntryDoesNotCount()
    {
        Entry("FEX-x86_64", "disabled", X86_64Magic, Mask);

        Probe().Should().Be(X86_64Support.None);
    }

    [Fact]
    public void TheOffsetLineShiftsWhereTheMagicIsCompared()
    {
        // The same entry starting at byte 2: it still takes x86-64 files, and an aarch64 one at the same offset does not.
        Entry("shifted-x86", "enabled", X86_64Magic[4..], Mask[4..], offset: 2);
        Probe().Should().Be(X86_64Support.Interpreter);

        File.Delete(Path.Combine(_directory, "shifted-x86"));
        Entry("shifted-arm", "enabled", Aarch64Magic[4..], Mask[4..], offset: 2);
        Probe().Should().Be(X86_64Support.None);
    }

    [Fact]
    public void TheMaskIsAppliedBeforeComparing()
    {
        // Byte 19 (the high byte of e_machine) is masked out, so whatever the magic holds there is not compared.
        var garbage = X86_64Magic[..38] + "ab";
        var mask = Mask[..38] + "00";
        Entry("FEX-x86_64", "enabled", garbage, mask);

        Probe().Should().Be(X86_64Support.Interpreter);
    }

    [Fact]
    public void AnEntryThatNeverLooksAtTheMachineCodeSaysNothingAboutX86_64()
    {
        Entry("any-elf", "enabled", "7f454c46", "ffffffff");

        Probe().Should().Be(X86_64Support.None);
    }

    [Fact]
    public void TheRegisterAndStatusFilesAreNotEntries()
    {
        File.WriteAllText(Path.Combine(_directory, "status"), "enabled\n");
        File.WriteAllText(Path.Combine(_directory, "register"), "enabled\n");

        Probe().Should().Be(X86_64Support.None);
    }

    [Fact]
    public void AMissingBinfmtDirectoryIsUnknownNotNone()
    {
        new HostExecution(Path.Combine(_directory, "not-mounted"), Architecture.Arm64).CanRunX86_64().Should().Be(X86_64Support.Unknown);
    }
}
