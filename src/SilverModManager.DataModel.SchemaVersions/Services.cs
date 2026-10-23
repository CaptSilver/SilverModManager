using Microsoft.Extensions.DependencyInjection;

namespace SilverModManager.DataModel.SchemaVersions;

public static class Services
{
    public static IServiceCollection AddMigrations(this IServiceCollection services)
    {
        services.AddSchemaVersionModel();
        services.AddMigrationLogItemModel();
        services.AddTransient<MigrationService>();

        // Migrations go here, numbered from _0001. Existing databases are at version 0 until the first one runs.
        services.AddMigration<_0001_KeyInstallsByGameId>();

        return services;
    }

    /// <summary>
    /// Add a migration to the DI container
    /// </summary>
    public static IServiceCollection AddMigration<T>(this IServiceCollection services) where T : IMigration
    {
        return services.AddSingleton<MigrationDefinition>(_ => new MigrationDefinition(T.IdAndName.Id, T.IdAndName.Name, typeof(T)))
            // Transient so that migrations can store data locally
            .AddTransient(typeof(T));
    }
}
