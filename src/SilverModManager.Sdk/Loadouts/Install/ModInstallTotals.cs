namespace SilverModManager.Sdk.Loadouts.Install;

/// <summary>How many mod folders there are and how many of them finished or failed.</summary>
public sealed record ModInstallTotals(int Total, int Installed, int Failed);
