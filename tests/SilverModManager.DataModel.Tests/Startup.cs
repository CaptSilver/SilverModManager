using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Backend;
using SilverModManager.Games.RedEngine;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit.DependencyInjection.Logging;

namespace SilverModManager.DataModel.Tests;

public static class Startup
{
    public static void ConfigureServices(IServiceCollection container)
    {
        ConfigureTestedServices(container);
        container.AddLogging(builder => builder.AddXunitOutput());
    }
    
    public static void ConfigureTestedServices(IServiceCollection container)
    {
        AddServices(container);
    }
    
    public static IServiceCollection AddServices(IServiceCollection container)
    {
        var baseDirectory = $"SilverModManager.DataModel.Tests-{Guid.NewGuid()}";

        var prefix = FileSystem.Shared
            .FromUnsanitizedFullPath(TestScratch.Combine(baseDirectory));

        container.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticEmitter, RecordingLoadoutEmitter>());
        container.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticEmitter, ScriptedLoadoutEmitter>());

        return container
            .AddGameServices()
            .AddLoadoutItemGroupPriorityModel()
            .AddLoadoutGroupRuleModel()
            .AddLoadoutFileWinnerModel()
            .AddSortOrderItemModel()
            .AddDefaultServicesForTesting()
            .AddUniversalGameLocator<Cyberpunk2077Game>(new Version("1.61"))
            .AddRedEngineGames()
            .AddLoadoutAbstractions()
            .Validate();
    }
}

