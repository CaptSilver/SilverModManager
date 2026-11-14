using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Every mount any scope in this process holds right now. Closing the window or replacing the app's own binary would
/// kill the process serving them while a game is reading through it, so both ask this first.
/// </summary>
public sealed class HeldMounts
{
    private readonly object _gate = new();
    private readonly List<HeldMount> _holds = [];

    public bool Any
    {
        get { lock (_gate) return _holds.Count > 0; }
    }

    public IReadOnlyList<(GameInstallation Installation, GamePath Root)> Current
    {
        get
        {
            lock (_gate) return _holds.Select(static h => (h.Installation, h.Root)).ToArray();
        }
    }

    /// <summary>
    /// Records a mount until the returned handle is disposed. Disposing twice is harmless.
    /// </summary>
    public IDisposable Hold(GameInstallation installation, GamePath root)
    {
        var hold = new HeldMount(this, installation, root);
        lock (_gate) _holds.Add(hold);
        return hold;
    }

    private void Release(HeldMount hold)
    {
        lock (_gate) _holds.Remove(hold);
    }

    private sealed class HeldMount(HeldMounts owner, GameInstallation installation, GamePath root) : IDisposable
    {
        public GameInstallation Installation => installation;

        public GamePath Root => root;

        public void Dispose() => owner.Release(this);
    }
}
