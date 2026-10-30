using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod nexus download-links</c> takes a mod id and a file id; both need an option parser or the verb
/// can never be run.
/// </summary>
public class NexusDownloadLinksVerbTests(IServiceProvider provider)
{
    [Fact]
    public void AModIdIsParsedFromANumber()
    {
        var parsed = provider.GetRequiredService<IOptionParser<ModId>>().TryParse("1234", out var value, out var error);

        parsed.Should().BeTrue(error);
        value.Value.Should().Be(1234u);
    }

    [Fact]
    public void AFileIdIsParsedFromANumber()
    {
        var parsed = provider.GetRequiredService<IOptionParser<FileId>>().TryParse("5678", out var value, out var error);

        parsed.Should().BeTrue(error);
        value.Value.Should().Be(5678u);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-1")]
    [InlineData("")]
    public void AnIdThatIsNotAnUnsignedNumberIsRefusedWithAReason(string text)
    {
        var modParsed = provider.GetRequiredService<IOptionParser<ModId>>().TryParse(text, out _, out var modError);
        var fileParsed = provider.GetRequiredService<IOptionParser<FileId>>().TryParse(text, out _, out var fileError);

        modParsed.Should().BeFalse();
        modError.Should().Contain($"`{text}`");
        fileParsed.Should().BeFalse();
        fileError.Should().Contain($"`{text}`");
    }

    [Fact]
    public async Task ABadModIdFailsTheVerbBeforeAnyRequestIsMade()
    {
        var renderer = new LoggingRenderer();

        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>()
            .RunAsync(["nexus", "download-links", "-g", "skyrim", "-m", "soon", "-f", "1"], renderer, CancellationToken.None);

        exitCode.Should().NotBe(0);
        string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)).Should().Contain("Invalid ModId `soon`");
    }
}
