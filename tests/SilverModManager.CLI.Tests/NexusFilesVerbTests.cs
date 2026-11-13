using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SilverModManager.Games.TestFramework;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Networking.NexusWebApi.Errors;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>nexus files</c> against a GraphQL client the test answers for, so nothing reaches Nexus Mods.
/// </summary>
[Collection("ConsoleOutput")]
public class NexusFilesVerbTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<NexusFilesVerbTests>(helper)
{
    private readonly IGraphQlClient _graphQl = Substitute.For<IGraphQlClient>();

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddVerbs().AddSingleton(_graphQl);

    private static IModFile FileOf(uint id, string name, ModFileCategory category)
    {
        var file = Substitute.For<IModFile>();
        file.Uid.Returns(new FileUid(FileId.From(id), NexusModsGameId.From(3333)).ToV2Api());
        file.Name.Returns(name);
        file.Version.Returns("1.0");
        file.Category.Returns(category);
        file.SizeInBytes.Returns("2048");
        file.Date.Returns(1_700_000_000);
        return file;
    }

    private void Answer(params IModFile[] files) => _graphQl.QueryModFiles(ModId.From(9), Arg.Any<NexusModsGameId>(), Arg.Any<CancellationToken>())
        .Returns(ValueTask.FromResult(new GraphQlResult<IModFile[], NotFound>(files)));

    [Fact]
    public async Task Files_ListsCategories()
    {
        var loadout = await CreateLoadout();
        Answer(FileOf(1, "Core", ModFileCategory.Main), FileOf(2, "Extras", ModFileCategory.OldVersion));

        var (code, output) = await NexusVerbHost.Run(ServiceProvider, "nexus", "files", "--loadout", loadout.Name, "--modId", "9");

        code.Should().Be(0, output);
        output.Should().Contain("File id").And.Contain("Category").And.Contain("Size").And.Contain("Date");
        output.Should().Contain("Core").And.Contain("MAIN").And.Contain("Extras").And.Contain("OLD_VERSION");
    }

    [Fact]
    public async Task Files_Json()
    {
        var loadout = await CreateLoadout();
        Answer(FileOf(1, "Core", ModFileCategory.Main));

        var (code, output) = await NexusVerbHost.Run(ServiceProvider, "nexus", "files", "--loadout", loadout.Name, "--modId", "9", "--json");

        code.Should().Be(0, output);
        var row = JsonDocument.Parse(output).RootElement[0];
        row.GetProperty("name").GetString().Should().Be("Core");
        row.GetProperty("sizeBytes").GetInt64().Should().Be(2048);
        row.GetProperty("category").GetString().Should().Be("MAIN");
        row.GetProperty("fileId").GetUInt32().Should().Be(1);
    }

    [Fact]
    public async Task Files_ConnectionFails_SaysNexusDidNotAnswer()
    {
        var loadout = await CreateLoadout();
        _graphQl.QueryModFiles(default, default, default).ThrowsAsyncForAnyArgs(new HttpRequestException("Connection refused"));

        var (code, output) = await NexusVerbHost.Run(ServiceProvider, "nexus", "files", "--loadout", loadout.Name, "--modId", "9");

        code.Should().Be(1, output);
        output.Should().Contain("Nexus Mods did not answer").And.Contain("Connection refused").And.NotContain("   at ");
    }

    [Fact]
    public async Task Files_NexusError_IsNotZeroFiles()
    {
        var loadout = await CreateLoadout();
        var outage = new UnknownError { Code = ErrorCode.From("SMM_UNKNOWN_ERROR"), Message = "bad gateway", ClientError = Substitute.For<StrawberryShake.IClientError>() };
        _graphQl.QueryModFiles(default, default, default)
            .ReturnsForAnyArgs(ValueTask.FromResult(new GraphQlResult<IModFile[], NotFound>([new KeyValuePair<ErrorCode, IGraphQlError>(outage.Code, outage)])));

        var (code, output) = await NexusVerbHost.Run(ServiceProvider, "nexus", "files", "--loadout", loadout.Name, "--modId", "9");

        code.Should().Be(1, output);
        output.Should().Contain("Nexus Mods did not answer").And.NotContain("File id");
    }

    [Fact]
    public async Task Files_AModNexusDoesNotKnowHasNone()
    {
        var loadout = await CreateLoadout();
        Answer();

        var (code, output) = await NexusVerbHost.Run(ServiceProvider, "nexus", "files", "--loadout", loadout.Name, "--modId", "9");

        code.Should().Be(0, output);
        output.Should().Contain("no files");
    }
}
