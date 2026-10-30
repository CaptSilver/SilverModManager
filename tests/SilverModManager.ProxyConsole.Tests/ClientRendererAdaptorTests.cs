using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nerdbank.Streams;
using SilverModManager.ProxyConsole.Messages;

namespace SilverModManager.ProxyConsole.Tests;

public class ClientRendererAdaptorTests(IServiceProvider provider)
{
    private const int TimeoutMs = 120_000;

    /// <summary>
    /// The adaptor starts reading as soon as it is constructed, and the first thing a main process sends is the
    /// question this answers. The answer has to hold the arguments however late the constructing thread gets to
    /// finish: a reply with none made the main process run an empty command and the client report success.
    /// Many adaptors are built at once so that threads are preempted inside the constructor.
    /// </summary>
    [Fact(Timeout = TimeoutMs)]
    public async Task TheAnswerToTheArgumentsQuestion_AlwaysCarriesTheArguments_EvenWhenConstructionIsPreempted()
    {
        // The real definitions: the union they register is process-wide, and an empty one would break every render after it.
        var definitions = provider.GetRequiredService<IEnumerable<IRenderableDefinition>>();
        var missing = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 4000),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 8, CancellationToken = TestContext.Current.CancellationToken },
            async (_, token) =>
            {
                var (near, far) = FullDuplexStream.CreatePair();
                var main = new Serializer(far, definitions);
                await main.SendAsync(new ProgramArgumentsRequest());

                var adaptor = new ClientRendererAdaptor(near, new LoggingRenderer(), provider, ["loadouts", "list"]);

                var reply = await main.ReceiveAsync();
                if (reply is not ProgramArgumentsResponse { Arguments: not null }) Interlocked.Increment(ref missing);

                await far.DisposeAsync();
                await adaptor.RunningTask.WaitAsync(TimeSpan.FromSeconds(30), token);
            });

        missing.Should().Be(0, "every reply to the arguments question has to carry the arguments the adaptor was given");
    }
}
