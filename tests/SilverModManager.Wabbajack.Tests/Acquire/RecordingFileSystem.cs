using System.Reflection;
using NexusMods.Paths;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>
/// Forwards every <see cref="IFileSystem"/> call to a real file system and writes down which method touched which path,
/// so a test can say which files a component looked at, not only what it did with them.
/// </summary>
public class RecordingFileSystem : DispatchProxy
{
    private readonly List<(string Method, string Path)> _calls = [];
    private IFileSystem _inner = null!;

    public static (IFileSystem FileSystem, RecordingFileSystem Log) Wrap(IFileSystem inner)
    {
        var proxy = Create<IFileSystem, RecordingFileSystem>();
        var log = (RecordingFileSystem)(object)proxy;
        log._inner = inner;
        return (proxy, log);
    }

    /// <summary>Every call made so far, for the paths given as arguments.</summary>
    public IReadOnlyList<(string Method, string Path)> Calls
    {
        get
        {
            lock (_calls) return _calls.ToArray();
        }
    }

    /// <summary>Runs on the calling thread after a call is written down and before it is forwarded; a test blocks in it to hold the caller at that call.</summary>
    public Action<string, string>? BeforeCall { get; set; }

    /// <summary>Writes down a touch that does not go through the file system, such as a link check made by the component itself.</summary>
    public void Note(string method, AbsolutePath path)
    {
        lock (_calls) _calls.Add((method, path.ToString()));
    }

    /// <summary>The distinct paths touched by any call.</summary>
    public IReadOnlySet<string> Paths => Calls.Select(c => c.Path).ToHashSet();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null) return null;
        lock (_calls)
        {
            foreach (var arg in args ?? [])
                if (arg is AbsolutePath path) _calls.Add((targetMethod.Name, path.ToString()));
        }

        var hook = BeforeCall;
        if (hook is not null)
            foreach (var arg in args ?? [])
                if (arg is AbsolutePath path) hook(targetMethod.Name, path.ToString());

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}
