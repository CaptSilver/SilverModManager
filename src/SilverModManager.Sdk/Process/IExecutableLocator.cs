using NexusMods.Paths;

namespace SilverModManager.Sdk;

/// <summary>Finds a program the way a shell would, so a test can say where (or whether) it is.</summary>
public interface IExecutableLocator
{
    /// <summary>The first executable called <paramref name="name"/> on <c>PATH</c>, or null.</summary>
    AbsolutePath? Find(string name);
}
