using JetBrains.Annotations;

namespace SilverModManager.Library;

/// <summary>
/// Names a GitHub repository either by its numeric id or by owner and name.
/// </summary>
[PublicAPI]
public readonly record struct GitHubRepositoryRef
{
    public ulong? Id { get; }
    public string? Organization { get; }
    public string? Repository { get; }

    private GitHubRepositoryRef(ulong? id, string? organization, string? repository)
    {
        Id = id;
        Organization = organization;
        Repository = repository;
    }

    public static GitHubRepositoryRef FromId(ulong id) => new(id, null, null);

    public static GitHubRepositoryRef FromName(string organization, string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        return new GitHubRepositoryRef(null, organization, repository);
    }

    public override string ToString() => Id is { } id ? $"repository {id}" : $"{Organization}/{Repository}";
}
