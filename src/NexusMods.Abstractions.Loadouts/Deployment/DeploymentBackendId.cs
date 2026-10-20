namespace NexusMods.Abstractions.Loadouts.Deployment;

/// <summary>
/// Names a deployment backend. The value is persisted in plan rows, so it must stay stable.
/// </summary>
public readonly record struct DeploymentBackendId(string Value)
{
    /// <summary>
    /// Writes files into the game folder, the behaviour before backends were selectable.
    /// </summary>
    public static readonly DeploymentBackendId Copy = new("copy");

    public override string ToString() => Value;
}
