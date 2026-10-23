using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>A file of the list the install could not write, with the reason.</summary>
public sealed record WabbajackSkippedRow(string To, string Reason, SkipKind Kind);
