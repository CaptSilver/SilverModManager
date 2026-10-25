namespace SilverModManager;

/// <summary>
/// Where tests put throwaway data. scripts/test.sh points <see cref="RootVariable"/> at a folder on tmpfs so a
/// run doesn't hammer the checkout's encrypted disk. Without it (an IDE run) everything lands beside the test
/// binaries as before.
/// </summary>
/// <remarks>
/// Compiled into every test assembly from tests/Directory.Build.props rather than shared through a project,
/// so it has no dependencies and every test project can use it.
/// </remarks>
internal static class TestScratch
{
    public const string RootVariable = "SILVER_TEST_SCRATCH";
    public const string ReflinkRootVariable = "SILVER_TEST_SCRATCH_REFLINK";
    public const string HostTempVariable = "SILVER_TEST_HOST_TMPDIR";

    /// <summary>
    /// Scratch folder for tests that don't care which filesystem they are on.
    /// </summary>
    public static string Root { get; } = Resolve(Environment.GetEnvironmentVariable(RootVariable), AppContext.BaseDirectory, RootVariable);

    /// <summary>
    /// Scratch folder on a filesystem that supports reflinks, for tests that deploy with or assert the reflink
    /// backend. tmpfs can't clone, so these can't use <see cref="Root"/>.
    /// </summary>
    public static string ReflinkRoot { get; } = Resolve(Environment.GetEnvironmentVariable(ReflinkRootVariable), AppContext.BaseDirectory, ReflinkRootVariable);

    /// <summary>
    /// The temp folder before test.sh redirected TMPDIR to tmpfs. Tests that need a device other than
    /// /dev/shm (hard links and symlinks across devices) place their "other disk" here.
    /// </summary>
    public static string HostTemp { get; } = ResolveHostTemp(Environment.GetEnvironmentVariable(HostTempVariable));

    /// <summary>
    /// A path under <see cref="Root"/>.
    /// </summary>
    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>
    /// A path under <see cref="ReflinkRoot"/>.
    /// </summary>
    public static string CombineReflink(params string[] parts) => Path.Combine([ReflinkRoot, .. parts]);

    internal static string Resolve(string? value, string fallback, string variable = RootVariable)
    {
        if (string.IsNullOrWhiteSpace(value)) return Path.TrimEndingDirectorySeparator(fallback);
        if (!Path.IsPathRooted(value)) throw new InvalidOperationException($"{variable} must be an absolute path, got '{value}'.");
        return Path.TrimEndingDirectorySeparator(value);
    }

    internal static string ResolveHostTemp(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Path.TrimEndingDirectorySeparator(Path.GetTempPath()) : Path.TrimEndingDirectorySeparator(value);
}
