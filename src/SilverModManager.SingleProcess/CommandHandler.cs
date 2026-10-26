using System.CommandLine.Invocation;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.SingleProcess;

/// <summary>
/// Command handler for linking a verb definition to the parser
/// </summary>
/// <param name="getters"></param>
/// <param name="methodInfo"></param>
internal class CommandHandler(IServiceProvider serviceProvider, List<Func<InvocationContext, object?>> getters, MethodInfo methodInfo)
    : ICommandHandler
{
    public int Invoke(InvocationContext context)
    {
        throw new NotSupportedException("Only async is supported");
    }

    public async Task<int> InvokeAsync(InvocationContext context)
    {
        try
        {
            // Resolve all the parameters
            var args = GC.AllocateUninitializedArray<object?>(getters.Count);
            for (var i = 0; i < getters.Count; i++)
            {
                args[i] = getters[i](context);
            }

            // Invoke the method
            return await (Task<int>)methodInfo.Invoke(null, args)!;
        }
        catch (Exception ex)
        {
            serviceProvider.GetRequiredService<ILogger<CommandHandler>>().LogFailure(ex, "An error occurred while executing the command {Command}", methodInfo.Name);

            // Only a verb that asks for the renderer gets one bound; any other still has to say why it failed.
            if (context.BindingContext.GetService<IRenderer>() is { } renderer) await renderer.Error(ex);
            else context.Console.Error.Write(CliErrors.Describe(ex) + Environment.NewLine);
            return CliErrors.ExitCode(ex);
        }
    }
}
