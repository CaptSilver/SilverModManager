using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.CLI.Types;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.SingleProcess;

/// <summary>
/// A configurator for the commandline parser. It looks for all verb definitions (created by AddVerb)
/// and adds them to the parser. It also adds all injected types to the binding context. The RunAsync method
/// can be used to run the parser and execute the matching verb.
/// </summary>
public class CommandLineConfigurator
{
    private readonly RootCommand _rootCommand;
    private readonly Type[] _injectedTypes;
    private readonly IServiceProvider _provider;
    private readonly MethodInfo _makeOptionMethod;

    private ILogger _logger;

    /// <summary>
    /// DI constructor
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="verbDefinitions"></param>
    public CommandLineConfigurator(IServiceProvider provider, IEnumerable<VerbDefinition> verbDefinitions, IEnumerable<ModuleDefinition> moduleDefinitions)
    {
        _provider = provider;
        _logger = provider.GetRequiredService<ILogger<CommandLineConfigurator>>();
        _makeOptionMethod = GetType().GetMethod(nameof(MakeOption), BindingFlags.Instance | BindingFlags.NonPublic)!;
        (_rootCommand, _injectedTypes) = MakeRootCommand(verbDefinitions, moduleDefinitions);
    }

    /// <summary>
    /// Builds the root command and returns it, along with the list of injected types.
    /// </summary>
    /// <param name="verbDefinitions"></param>
    /// <returns></returns>
    private (RootCommand, Type[]) MakeRootCommand(IEnumerable<VerbDefinition> verbDefinitions, IEnumerable<ModuleDefinition> moduleDefinitions)
    {
        var rootCommand = new RootCommand();
        var injectedTypes = new HashSet<Type>();

        var modules = new Dictionary<string, Command>();

        // Create the modules first so we can tie verbs to them
        foreach (var module in moduleDefinitions.OrderBy(v => v.Name.Length).ThenBy(v => v.Name.Last()))
        {
            if (modules.ContainsKey(module.Name))
                throw new InvalidOperationException($"Module {module.Name} already exists can't define it again");
            
            var nameParts = module.Name.Split(" ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var localName = nameParts[^1];
            var command = new Command(localName, module.Description);
            modules.Add(module.Name, command);

            if (nameParts.Length > 1)
            {
                var parentName = string.Join(" ", nameParts[..^1]);
                if (!modules.TryGetValue(parentName, out var parent))
                    throw new InvalidOperationException($"Parent module {module.Name} does not exist");
                
                parent.AddCommand(command);
            }
            else
            {
                rootCommand.AddCommand(command);
            }
            
        }

        foreach (var verbDefinition in verbDefinitions.OrderBy(v => v.Name.Last()))
        {
            Command parentCommand = rootCommand;
            var nameParts = verbDefinition.Name.Split(" ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (nameParts.Length > 1)
            {
                var moduleName = string.Join(" ", nameParts[..^1]);
                if (!modules.TryGetValue(moduleName, out var moduleCommand))
                    throw new InvalidOperationException($"Module {moduleName} does not exist");
                parentCommand = moduleCommand;
            }

            var command = new Command(nameParts[^1], verbDefinition.Description);
            var getters = new List<Func<InvocationContext, object?>>();

            foreach (var optionDefinition in verbDefinition.Options)
            {
                if (optionDefinition.IsInjected)
                {
                    // Injected options are pulled from the service provider via the binding context
                    injectedTypes.Add(optionDefinition.Type);
                    getters.Add(ctx => ctx.BindingContext.GetRequiredService(optionDefinition.Type));
                }
                else
                {
                    var option = (Option)_makeOptionMethod.MakeGenericMethod(optionDefinition.Type)
                        .Invoke(this, [optionDefinition])!;

                    option.IsRequired = !optionDefinition.IsOptional;
                    
                    command.AddOption(option);
                    getters.Add(ctx => ctx.ParseResult.GetValueForOption(option));
                }
            }
            command.Handler = new CommandHandler(_provider, getters, verbDefinition.Info);

            parentCommand.AddCommand(command);
        }
        rootCommand.AddCommand(MakeRunHelpCommand());
        rootCommand.AddCommand(MakeStatusHelpCommand());
        return (rootCommand, injectedTypes.ToArray());
    }

    /// <summary>
    /// Exit code of a failed launch, the same one the launch session uses.
    /// </summary>
    private const int LaunchFailureExitCode = 125;

    private const string RunSynopsis = "run <loadout> [--game <appid|name>] [--dry-run] -- <command> [args...]";

    /// <summary>
    /// <c>run</c> never reaches the verb parser: the process start-up takes it before any verb is parsed. This
    /// command only puts it in <c>--help</c>. Running it here means the start-up routing was bypassed, which
    /// is reported rather than ignored.
    /// </summary>
    private static Command MakeRunHelpCommand()
    {
        var command = new Command("run", $"Run a command against a loadout, as from a Steam launch option: {RunSynopsis}")
        {
            new Argument<string>("loadout", "The name of the loadout to run"),
            new Option<string>("--game", "The Steam app id or name of the game, when the loadout name is not unique"),
            new Option<bool>("--dry-run", "Print the command that would be started and exit, without locking, applying or starting anything"),
            new Argument<string[]>("command", "The command to run, after '--'") { Arity = ArgumentArity.ZeroOrMore },
        };
        command.TreatUnmatchedTokensAsErrors = false;
        command.SetHandler(context =>
        {
            context.Console.Error.Write($"The run command is not handled by this process. usage: silvermod {RunSynopsis}");
            context.ExitCode = LaunchFailureExitCode;
        });
        return command;
    }

    /// <summary>
    /// <c>status</c> is answered by the process start-up before any host exists, so like <c>run</c> this only
    /// puts it in <c>--help</c>.
    /// </summary>
    private static Command MakeStatusHelpCommand()
    {
        var command = new Command("status", "Show the games launched through SilverModManager and any launch that died without cleaning up: status [--json]")
        {
            new Option<bool>("--json", "Print the report as JSON"),
        };
        command.SetHandler(context =>
        {
            context.Console.Error.Write("The status command is not handled by this process. usage: silvermod status [--json]");
            context.ExitCode = 1;
        });
        return command;
    }

    private Option MakeOption<T>(OptionDefinition optionDefinition) where T : notnull
    {
        var aliases = new[] { "-" + optionDefinition.ShortName, "--" + optionDefinition.LongName };

        ParseArgument<T> parser = result =>
        {
            // A flag given without a value, such as --write, is true. Only an explicit --write false is parsed.
            if (typeof(T) == typeof(bool) && result.Tokens.Count == 0) return (T)(object)true;

            var service = _provider.GetService<IOptionParser<T>>();
            if (service is null) return default!;
            if (service.TryParse(result.Tokens.Single().Value, out var itm, out var error)) return itm;
            result.ErrorMessage = error;
            return default!;
        };

        var option = new Option<T>(aliases, parser, false, optionDefinition.HelpText);
        return option;
    }

    private async Task<bool> RunLink(string[] args, CancellationToken cancellationToken)
    {
        // NOTE(erri120): The CLI framework we're using was designed for verbs and options only.
        // This method circumvents the entire framework to deal with this invocation:
        // SilverModManager nxm://something
        // This invocation can't be parsed with the CLI framework, so we hand-role the parsing.
        // See https://github.com/Nexus-Mods/NexusMods.App/issues/1677 for details.

        if (args.Length != 1) return false;
        if (!Uri.TryCreate(args[0], UriKind.Absolute, out var uri)) return false;

        var handlers = _provider.GetServices<IIpcProtocolHandler>().ToArray();
        var handler = handlers.FirstOrDefault(handler => handler.Protocol.Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase));

        if (handler is null) return false;

        try
        {
            _logger.LogInformation("Using handler {Handler} for `{Scheme}`", handler.GetType(), uri.Scheme);
            await handler.Handle(uri.ToString(), cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception while running handler {Handler} for `{Uri}`", handler.GetType(), uri);
        }

        return true;
    }

    /// <summary>
    /// Runs the commandline parser and executes the verb using the given renderer and arguments.
    /// </summary>
    /// <param name="args"></param>
    /// <param name="renderer"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    public async Task<int> RunAsync(string[] args, IRenderer renderer, CancellationToken token)
    {
        if (await RunLink(args, token)) return 0;
        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Received command `{Command}`", string.Join(" ", args));

        var parser = new CommandLineBuilder(_rootCommand)
            .UseHelp()
            .UseParseErrorReporting()
            .AddMiddleware((ctx, next) =>
            {
                foreach (var type in _injectedTypes)
                {
                    if (type == typeof(IRenderer))
                        ctx.BindingContext.AddService(type, _ => renderer);
                    else if (type == typeof(CancellationToken))
                        ctx.BindingContext.AddService(type, _ => token);
                    else
                        ctx.BindingContext.AddService(type, _ => _provider.GetRequiredService(type));
                }
                return next(ctx);
            })
            .Build();

        return await parser.InvokeAsync(args, new ConsoleToRendererAdapter(renderer));
    }
}
