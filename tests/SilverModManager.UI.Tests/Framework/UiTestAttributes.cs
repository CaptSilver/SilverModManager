using System.Runtime.CompilerServices;

namespace SilverModManager.UI.Tests.Framework;

/// <summary>
/// A <c>[Fact]</c> that gives up after <see cref="Limit"/> instead of waiting for ever. Every <c>[Fact]</c> in this
/// project means this one (see Usings.cs): a test that awaits a UI thread that has stopped running jobs would
/// otherwise hold the whole test host with no output.
/// </summary>
public sealed class UiFactAttribute : Xunit.FactAttribute
{
    /// <summary>The longest any UI test may run, in milliseconds; the slowest legitimate ones take seconds.</summary>
    public const int Limit = 240_000;

    public UiFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Timeout = Limit;
    }
}

/// <summary>A <c>[Theory]</c> with the same limit as <see cref="UiFactAttribute"/>.</summary>
public sealed class UiTheoryAttribute : Xunit.TheoryAttribute
{
    public UiTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Timeout = UiFactAttribute.Limit;
    }
}
