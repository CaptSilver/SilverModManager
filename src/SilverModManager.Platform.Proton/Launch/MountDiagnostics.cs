using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Platform.Proton.Launch;

internal static partial class MountDiagnostics
{
    private const string Source = "SilverModManager.Platform.Proton";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate FuseUnavailableForMountedRoot = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 4))
        .WithTitle("A Mounted Folder Cannot Be Mounted")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Root} cannot be mounted: {Reason}")
        .WithDetails("""
{GameName} is set to serve {Root} through a mounted folder, and the game will not start from SilverModManager until the folder can be mounted again.

To deploy the folder another way, run `silvermodmanager deployment --game "{GameName}" --root {Root} --use {Pick}`.
`silvermodmanager doctor` lists each check FUSE needs.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("Root")
            .AddValue<string>("Reason")
            .AddValue<string>("Pick")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MountRecoveryBlocked = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 5))
        .WithTitle("Files A Game Wrote Could Not Be Promoted")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("Files the game wrote under {Root} could not be promoted; they are in {Recovery}")
        .WithDetails("""
{GameName} wrote files into {Root} while it ran from a mounted folder. Some of them changed again while they were
being moved into the loadout, so none were taken in. They were set aside in {Recovery}, with a README that says why.
Copy what you want to keep back into the game folder or a mod, then delete that folder.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("Root")
            .AddValue<string>("Recovery")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MountRecoveryNeedsLoadout = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 6))
        .WithTitle("Files A Game Wrote Wait For A Loadout")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("Files the game wrote under {Root} are waiting in {Staging}; choose the loadout they belong to")
        .WithDetails("""
{GameName} wrote files into {Root} while it ran from a mounted folder, and the session ended before they were
moved into a loadout. The app does not guess which loadout the game was running, since the one applied last may
not be it. Pick the loadout they belong to and they are added to its overrides.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("Root")
            .AddValue<string>("Staging")
        )
        .Finish();
}
