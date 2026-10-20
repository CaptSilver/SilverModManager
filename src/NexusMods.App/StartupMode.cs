namespace NexusMods.App;

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
    /// True for <c>run</c>: the process is the main process for the lifetime of one game
    /// launch. It must never take the slim-client path, because it holds the database and
    /// the launch session until the game exits.
    /// </summary>
    public bool IsHeadlessRun { get; set; } = false;

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
        else if (arg.Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            mode.RunAsMain = true;
            mode.ShowUI = false;
            mode.IsHeadlessRun = true;
            mode.Args = args[1..];
            mode.ExecuteCli = false;
        }
        else
        {
            mode.RunAsMain = false;
            mode.ShowUI = false;
        }

        return mode;
    }
}
