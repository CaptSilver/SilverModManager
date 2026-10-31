using System.Diagnostics;
using FluentAssertions;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The check that decides whether a live process's slot in the sync file is cleared. A wrong "no" empties a live
/// window's entry, so each answer is pinned against a real process.
/// </summary>
public class IsOurProcessTests
{
    [Fact]
    public void TheCurrentProcess_IsOurs()
    {
        using var current = Process.GetCurrentProcess();

        SilverModManager.App.Program.IsOurProcess(current).Should().BeTrue();
    }

    [Fact]
    public void AProcessWithAnotherName_IsNotOurs()
    {
        using var other = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        try
        {
            SilverModManager.App.Program.IsOurProcess(other).Should().BeFalse();
        }
        finally
        {
            other.Kill();
            other.WaitForExit();
        }
    }

    [Fact]
    public void AProcessThatHasExited_CountsAsOurs_BecauseItsNameProvesNothing()
    {
        using var gone = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!;
        gone.WaitForExit();

        SilverModManager.App.Program.IsOurProcess(gone).Should().BeTrue();
    }
}
