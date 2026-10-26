using SilverModManager.Abstractions.Secrets;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;

namespace SilverModManager.App;

public class StartupMode
{
    /// <summary>
    /// The CLI arguments
    /// </summary>
    public string[] Args { get; set; } = [];
    
    /// <summary>
    /// The original unprocessed CLI arguments
    /// </summary>
    public string[] OriginalArgs { get; set; } = [];
    
    /// <summary>
    /// If true, the app will assume it is the main application and will not try to connect to another instance
    /// </summary>
    public bool RunAsMain { get; set; } = false;
    
    /// <summary>
    /// If true, the app will execute the CLI commands, otherwise the arguments will be ignored
    /// </summary>
    public bool ExecuteCli { get; set; } = false;
    
    /// <summary>
    /// If true, the Avalonia UI will be shown
    /// </summary>
    public bool ShowUI { get; set; } = true;
    
    public bool IsAvaloniaDesigner { get; set; } = false;

    /// <summary>
    /// A secrets file named on the command line (<c>--secrets-file</c>); it wins over <c>SILVERMM_SECRETS_FILE</c>.
    /// </summary>
    public AbsolutePath? SecretsFile { get; set; }

    /// <summary>
    /// Why the <c>--secrets-file</c> option of a credential verb could not be read, or null. It is reported before
    /// any host is built, because the option decides how the host's secret store is made.
    /// </summary>
    public string? SecretsFileError { get; set; }

    /// <summary>
    /// True for the verbs that use this process's own credentials and nobody else's (<c>nexus-whoami</c>,
    /// <c>secrets-status</c>). Run in another process they would answer from that process's store, which never
    /// saw this process's <c>--secrets-file</c>.
    /// </summary>
    public bool UsesOwnCredentials { get; set; } = false;

    /// <summary>
    /// Overrides the user's secret storage setting. Test hosts set <see cref="SecretBackendChoice.Memory"/>
    /// so they never reach the machine's keyring.
    /// </summary>
    public SecretBackendChoice? SecretBackend { get; set; }

    /// <summary>
    /// True for <c>run</c>: the process is the main process for the lifetime of one game
    /// launch. It must never take the slim-client path, because it holds the database and
    /// the launch session until the game exits.
    /// </summary>
    public bool IsHeadlessRun { get; set; } = false;

    /// <summary>
    /// True for <c>status</c>: it only reads the launch lock files, so no host, database or sync file is involved.
    /// </summary>
    public bool IsStatus { get; set; } = false;

    /// <summary>
    /// True for <c>mcp</c>: the process serves MCP over stdio and talks to the running window like any slim client,
    /// but it never starts, kills or waits for a main process, and stdout belongs to the protocol.
    /// </summary>
    public bool IsMcp { get; set; } = false;

    /// <summary>
    /// True for <c>mcp --read-only</c>: the server leaves out the tools that change anything.
    /// </summary>
    public bool McpReadOnly { get; set; } = false;

    /// <summary>
    /// The usage line for an <c>mcp</c> start with an argument it does not know, or null. It is reported before any
    /// host is built; handing the arguments on as a verb would run them inside the window.
    /// </summary>
    public string? McpUsageError { get; set; }

    public const string McpUsage = "Usage: silvermodmanager mcp [--read-only]";

    /// <summary>
    /// True for <c>run --help</c> / <c>run -h</c> with the flag before any <c>--</c>: it prints the usage and exits,
    /// so no host is built. <c>run name -- game.exe --help</c> is still a launch, because the flag belongs to the game.
    /// </summary>
    public bool IsRunHelp { get; set; } = false;

    /// <summary>
    /// True for the hidden <c>selftest-helper</c> entry points the selftest spawns to check flock and signal
    /// behaviour on the shipped binary. They run before any host exists, so no sync file or database is involved.
    /// </summary>
    public bool IsSelftestHelper { get; set; } = false;

    /// <summary>
    /// Whether a client that finds no main process must start one that shows the UI.
    /// Only a protocol link needs this: the user clicked a link in a browser and expects the
    /// app to come up. Every other verb (including --help) is plain terminal use and must
    /// never open a window.
    /// </summary>
    public bool StartsUiWhenNoMainRunning =>
        Args.Length > 0 && Args[0].Equals("protocol-invoke", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Arguments that make a new process run <see cref="Args"/> as a headless main process,
    /// which executes the verb against the database it owns and then exits.
    /// </summary>
    public string[] HeadlessMainArgs => ["as-main", .. Args];
    
    public static StartupMode Parse(string[] args)
    {
        var mode = new StartupMode
        {
            Args = args, 
            OriginalArgs = args, 
            ExecuteCli = true,
        };

        if (args.Length == 0)
        {
            mode.ShowUI = true;
            mode.RunAsMain = true;
            mode.ExecuteCli = false;
            return mode;
        }

        var arg = args[0];
        if (arg.Equals("as-main", StringComparison.OrdinalIgnoreCase))
        {
            mode.RunAsMain = true;
            mode.ShowUI = false;
            mode.Args = args[1..];
            mode.ExecuteCli = args.Length > 1;
        } else if (arg.Equals("as-main-ui", StringComparison.OrdinalIgnoreCase))
        {
            mode.RunAsMain = true;
            mode.ShowUI = true;
            mode.Args = args[1..];
            mode.ExecuteCli = args.Length > 1;
        }
        else if (arg.Equals("run", StringComparison.OrdinalIgnoreCase) && AsksForHelpBeforeSeparator(args[1..]))
        {
            mode.RunAsMain = false;
            mode.ShowUI = false;
            mode.IsRunHelp = true;
            mode.Args = args[1..];
            mode.ExecuteCli = false;
        }
        else if (arg.Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            mode.RunAsMain = true;
            mode.ShowUI = false;
            mode.IsHeadlessRun = true;
            mode.Args = args[1..];
            mode.ExecuteCli = false;
        }
        else if (arg.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            mode.RunAsMain = false;
            mode.ShowUI = false;
            mode.IsStatus = true;
            mode.Args = args[1..];
            mode.ExecuteCli = false;
        }
        else if (arg.Equals("mcp", StringComparison.OrdinalIgnoreCase))
        {
            mode.RunAsMain = false;
            mode.ShowUI = false;
            mode.IsMcp = true;
            mode.ExecuteCli = false;
            mode.Args = [];
            ReadMcpOptions(mode, args[1..]);
        }
        else if (arg.Equals("selftest-helper", StringComparison.OrdinalIgnoreCase))
        {
            mode.RunAsMain = false;
            mode.ShowUI = false;
            mode.IsSelftestHelper = true;
            mode.Args = args[1..];
            mode.ExecuteCli = false;
        }
        else
        {
            mode.RunAsMain = false;
            mode.ShowUI = false;
        }

        ReadCredentialVerb(mode);
        return mode;
    }

    private static void ReadMcpOptions(StartupMode mode, string[] options)
    {
        foreach (var option in options)
        {
            if (option == "--read-only") mode.McpReadOnly = true;
            else
            {
                mode.McpUsageError = $"Unknown argument '{option}'. {McpUsage}";
                return;
            }
        }
    }

    private static readonly string[] CredentialVerbs = ["nexus-whoami", "secrets-status"];

    // The option stays in Args: a verb that is forwarded to a new headless main is started from them, and
    // that process has to read the option again to build its own store.
    private static void ReadCredentialVerb(StartupMode mode)
    {
        if (!mode.ExecuteCli || mode.IsHeadlessRun || mode.IsRunHelp || mode.IsStatus || mode.IsSelftestHelper || mode.IsMcp) return;
        if (!CredentialVerbs.Contains(mode.Args[0], StringComparer.Ordinal)) return;

        mode.UsesOwnCredentials = true;
        if (SecretsFileOption.TryFind(mode.Args.AsSpan(1).ToArray(), out var path, out var error)) mode.SecretsFile = path;
        else mode.SecretsFileError = error;
    }

    private static bool AsksForHelpBeforeSeparator(string[] runArgs)
    {
        foreach (var arg in runArgs)
        {
            if (arg == "--") return false;
            if (arg is "--help" or "-h") return true;
        }
        return false;
    }
}
