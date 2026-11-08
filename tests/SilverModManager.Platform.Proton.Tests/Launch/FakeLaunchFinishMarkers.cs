using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tests.Launch;

/// <summary>
/// In-memory <see cref="ILaunchFinishMarkers"/> for tests that drive a launch without a database. <see cref="Clear"/>
/// stands in for the retract a committed sync does.
/// </summary>
internal sealed class FakeLaunchFinishMarkers : ILaunchFinishMarkers
{
    public (LoadoutId Loadout, Guid SessionId, IReadOnlyList<string> GateNames)? Written { get; private set; }
    public int Retracts { get; private set; }
    public int RetractsThatFoundOne { get; private set; }
    public Exception? WriteFailure { get; set; }
    public Exception? RetractFailure { get; set; }

    /// <summary>What <see cref="Read"/> answers. Left null, Read throws so a test cannot get a silent "nothing pending".</summary>
    public Func<PendingLaunchFinish?>? ReadWith { get; set; }

    public bool IsSet => Written is not null;

    public void Clear() => Written = null;

    public Task Write(GameInstallation installation, LoadoutId loadout, Guid sessionId, IReadOnlyList<string> gateNames)
    {
        if (WriteFailure is not null) return Task.FromException(WriteFailure);
        Written = (loadout, sessionId, gateNames);
        return Task.CompletedTask;
    }

    // Not backed by Written on purpose: a test that reads a marker back says what it should see.
    public PendingLaunchFinish? Read(GameInstallation installation) =>
        ReadWith is { } read ? read() : throw new NotSupportedException("FakeLaunchFinishMarkers needs ReadWith to support Read; otherwise assert on Written instead.");

    public Task<bool> Retract(GameInstallation installation)
    {
        Retracts++;
        if (RetractFailure is not null) return Task.FromException<bool>(RetractFailure);
        var found = Written is not null;
        if (found) RetractsThatFoundOne++;
        Written = null;
        return Task.FromResult(found);
    }
}
