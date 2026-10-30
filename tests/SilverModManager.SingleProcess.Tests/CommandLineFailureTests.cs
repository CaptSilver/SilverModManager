using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.SingleProcess.Tests;

public class CommandLineFailureTests
{
    public sealed class UnregisteredOption;

    private sealed class RecordingRenderer : IRenderer
    {
        private readonly StringBuilder _output = new();
        public string Output => _output.ToString();

        public ValueTask RenderAsync(IRenderable renderable)
        {
            if (renderable is Text text) _output.Append(string.Format(text.Template, text.Arguments));
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync() => ValueTask.CompletedTask;
    }

    private sealed class ExceptionLog : ILoggerProvider, ILogger
    {
        public List<Exception> Exceptions { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Exceptions.Add(exception);
        }

        public void Dispose() { }
    }

    public static Task<int> ReturnsNothing() => null!;

    public static Task<int> TakesAnOption(UnregisteredOption option) => Task.FromResult(0);

    private static CommandLineConfigurator Configure(ExceptionLog log, params VerbDefinition[] verbs)
    {
        var services = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(log)).BuildServiceProvider();
        return new CommandLineConfigurator(services, verbs, []);
    }

    [Fact]
    public async Task OptionWithNoRegisteredParser_FailsTheCommandAndNamesTheOptionType()
    {
        var verb = new VerbDefinition("take", "", typeof(CommandLineFailureTests).GetMethod(nameof(TakesAnOption))!,
            [new OptionDefinition(typeof(UnregisteredOption), "o", "option", "", IsInjected: false, IsOptional: false)]);
        var renderer = new RecordingRenderer();

        var exitCode = await Configure(new ExceptionLog(), verb).RunAsync(["take", "--option", "x"], renderer, CancellationToken.None);

        exitCode.Should().NotBe(0);
        renderer.Output.Should().Contain(nameof(UnregisteredOption));
    }

    [Fact]
    public async Task VerbThatReturnsNoTask_FailsTheCommandAndNamesTheMethod()
    {
        var verb = new VerbDefinition("nothing", "", typeof(CommandLineFailureTests).GetMethod(nameof(ReturnsNothing))!, []);
        var log = new ExceptionLog();

        var exitCode = await Configure(log, verb).RunAsync(["nothing"], new RecordingRenderer(), CancellationToken.None);

        exitCode.Should().NotBe(0);
        log.Exceptions.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain(nameof(ReturnsNothing));
    }
}
