using SilverModManager.Sdk.Errors;

namespace SilverModManager.Sdk;

/// <summary>
/// The kernel refused to start a program with "Exec format error": it is built for another CPU and nothing
/// registered with the kernel can run it. Typically an x86-64 tool on an Arm machine without an x86-64 interpreter.
/// </summary>
public sealed class ProgramNotRunnableException(string file, Exception inner) : UserFacingException(
    $"{MarkdownText.Code(file)} is an x86-64 program and this machine could not start it; it needs an x86-64 interpreter such as FEX registered with the kernel.",
    inner)
{
    /// <inheritdoc />
    public override string Title => "A program cannot run on this machine";
}
