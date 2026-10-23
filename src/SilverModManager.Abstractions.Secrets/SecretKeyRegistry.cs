using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SilverModManager.Abstractions.Secrets;

/// <summary>
/// The secret keys the application's projects registered. The overlay looks for environment
/// overrides under these names.
/// </summary>
public interface ISecretKeyRegistry
{
    IReadOnlyCollection<SecretKey> Keys { get; }
}

/// <summary>
/// One call to <see cref="SecretKeyRegistryExtensions.AddSecretKeys"/>.
/// </summary>
public sealed record SecretKeyRegistration(IReadOnlyList<SecretKey> Keys);

public sealed class SecretKeyRegistry : ISecretKeyRegistry
{
    public SecretKeyRegistry(IEnumerable<SecretKey> keys)
    {
        var distinct = keys.Distinct().ToArray();
        ThrowOnSharedEnvironmentVariable(distinct);
        Keys = distinct;
    }

    public IReadOnlyCollection<SecretKey> Keys { get; }

    /// <summary>
    /// Two keys that map to the same environment variable (<c>a.b-c</c> and <c>a.b.c</c>) could not be
    /// overridden separately, so the clash is a registration error.
    /// </summary>
    public static void ThrowOnSharedEnvironmentVariable(IEnumerable<SecretKey> keys)
    {
        var byVariable = new Dictionary<string, SecretKey>(StringComparer.Ordinal);
        foreach (var key in keys.Distinct())
        {
            if (byVariable.TryGetValue(key.EnvironmentVariable, out var other))
                throw new InvalidOperationException($"Secret keys '{other}' and '{key}' both map to the environment variable {key.EnvironmentVariable}.");
            byVariable.Add(key.EnvironmentVariable, key);
        }
    }
}

public static class SecretKeyRegistryExtensions
{
    /// <summary>
    /// Registers the keys a project stores, so environment overrides for them are recognised.
    /// </summary>
    public static IServiceCollection AddSecretKeys(this IServiceCollection services, params SecretKey[] keys)
    {
        services.AddSingleton(new SecretKeyRegistration(keys));
        services.TryAddSingleton<ISecretKeyRegistry>(sp => new SecretKeyRegistry(sp.GetServices<SecretKeyRegistration>().SelectMany(r => r.Keys)));
        return services;
    }

    /// <summary>
    /// The keys registered so far, for checks made while the collection is still being built.
    /// </summary>
    public static IEnumerable<SecretKey> RegisteredSecretKeys(this IServiceCollection services)
        => services.Select(d => d.ImplementationInstance).OfType<SecretKeyRegistration>().SelectMany(r => r.Keys);
}
