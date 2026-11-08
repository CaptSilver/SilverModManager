using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Backend.Process;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Backend.Tests.Process;

public sealed class ProcessRunnerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "smm-process-runner-" + Guid.NewGuid().ToString("N"));

    public ProcessRunnerTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task AnExecFormatErrorBecomesAReadableRefusal()
    {
        var path = await WriteUnrunnableProgram();

        var act = async () => await CreateRunner().RunAsync(Cli.Wrap(path), logOutput: false);

        var thrown = (await act.Should().ThrowAsync<ProgramNotRunnableException>()).Which;
        thrown.Message.Should().Contain(path);
        thrown.Title.Should().NotBeNullOrWhiteSpace();
        thrown.InnerException.Should().NotBeNull();
    }

    [Fact]
    public async Task AnExecFormatErrorStartingAProcessObjectBecomesAReadableRefusal()
    {
        var path = await WriteUnrunnableProgram();
        using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false } };

        var act = async () => await CreateRunner().RunAsync(process);

        var thrown = (await act.Should().ThrowAsync<ProgramNotRunnableException>()).Which;
        thrown.Message.Should().Contain(path);
        thrown.InnerException.Should().NotBeNull();
    }

    [Fact]
    public async Task OtherStartFailuresKeepTheirException()
    {
        var path = Path.Combine(_folder, "does-not-exist");

        var act = async () => await CreateRunner().RunAsync(Cli.Wrap(path), logOutput: false);

        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        thrown.Should().NotBeOfType<ProgramNotRunnableException>();
    }

    // An ELF header whose machine field (bytes 18-19) is 0xffff, which no binfmt entry matches, so the
    // kernel answers ENOEXEC.
    private async Task<string> WriteUnrunnableProgram()
    {
        var header = new byte[64];
        new byte[] { 0x7f, 0x45, 0x4c, 0x46, 0x02, 0x01, 0x01, 0x00 }.CopyTo(header, 0);
        header[18] = 0xff;
        header[19] = 0xff;
        var path = Path.Combine(_folder, "not-a-program");
        await File.WriteAllBytesAsync(path, header);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static IProcessRunner CreateRunner()
    {
        var services = new ServiceCollection()
            .AddSingleton<ILogger<ProcessRunner>>(NullLogger<ProcessRunner>.Instance)
            .AddSingleton<IFileSystem>(new InMemoryFileSystem(OSInformation.FakeUnix))
            .AddSingleton(Substitute.For<ISettingsManager>())
            .BuildServiceProvider();
        return new ProcessRunner(services);
    }
}
