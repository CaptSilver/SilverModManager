using System.Diagnostics;

namespace NexusMods.Platform.Proton.Tests.Launch;

/// <summary>
/// Runs the silvermod-testhost helper, which owns every scenario that needs a second process.
/// </summary>
internal static class TestHost
{
    internal static readonly string Dll = Locate();

    /// <summary>
    /// The helper is built through a ProjectReference without referencing its assembly, so its output
    /// sits beside this project's under tests/.
    /// </summary>
    private static string Locate()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var helper = Path.Combine(dir.FullName, "silvermod-testhost", "bin");
            if (!Directory.Exists(helper)) continue;

            var dll = Directory.EnumerateFiles(helper, "silvermod-testhost.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (dll is not null) return dll;
        }
        throw new FileNotFoundException("silvermod-testhost.dll not found; build tests/silvermod-testhost");
    }

    public static Process Start(params string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Dll);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("Failed to start silvermod-testhost");
    }

    /// <summary>
    /// Runs the helper to completion.
    /// </summary>
    public static (int ExitCode, string Output) Run(params string[] args)
    {
        using var process = Start(args);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim() + (error.Length > 0 ? "\n" + error : ""));
    }

    /// <summary>
    /// Starts a helper that holds the lock and waits until it says it has it.
    /// </summary>
    public static Process StartHolder(string lockPath, int seconds)
    {
        var process = Start("hold-lock", lockPath, seconds.ToString());
        var line = process.StandardOutput.ReadLine();
        if (line != "locked")
        {
            process.Kill();
            throw new InvalidOperationException($"holder did not take the lock: '{line}' {process.StandardError.ReadToEnd()}");
        }
        return process;
    }
}
