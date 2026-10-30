using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.GuidedInstallers.ValueObjects;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Resources;

namespace SilverModManager.Games.FOMOD.UI;

public class GuidedInstallerStepDesignViewModel : GuidedInstallerStepViewModel
{
    public GuidedInstallerStepDesignViewModel() : base(
        NullLogger<GuidedInstallerStepViewModel>.Instance,
        new NoImages<Uri>(),
        new NoImages<FileIdentity>())
    {
        InstallationStep = SetupInstallationStep();
    }

    /// <summary>
    /// The designer has no image sources. The view model logs a failed load and shows no image, which is what
    /// the previewer should show.
    /// </summary>
    private sealed class NoImages<TIdentifier> : IResourceLoader<TIdentifier, Bitmap> where TIdentifier : notnull
    {
        public ValueTask<Resource<Bitmap>> LoadResourceAsync(TIdentifier resourceIdentifier, CancellationToken cancellationToken)
            => throw new NotSupportedException("The design-time view model has no images.");
    }

    private static GuidedInstallationStep SetupInstallationStep()
    {
        return new GuidedInstallationStep
        {
            Id = StepId.From(Guid.NewGuid()),
            Name = "Test Step",
            HasNextStep = false,
            HasPreviousStep = false,
            Groups = new[]
            {
                new OptionGroup
                {
                    Id = GroupId.From(Guid.NewGuid()),
                    Name = "Group 1 (Any)",
                    Type = OptionGroupType.Any,
                    Options = GuidedInstallerGroupDesignViewModel.GenerateAllOptionTypes()
                },
                new OptionGroup
                {
                    Id = GroupId.From(Guid.NewGuid()),
                    Name = "Group 2 (Exactly One)",
                    Type = OptionGroupType.ExactlyOne,
                    Options = GuidedInstallerGroupDesignViewModel.GenerateOptions()
                },
                new OptionGroup
                {
                    Id = GroupId.From(Guid.NewGuid()),
                    Name = "Group 3 (At Most One)",
                    Type = OptionGroupType.AtMostOne,
                    Options = GuidedInstallerGroupDesignViewModel.GenerateOptions()
                },
                new OptionGroup
                {
                    Id = GroupId.From(Guid.NewGuid()),
                    Name = "Group 4 (At Least One)",
                    Type = OptionGroupType.AtLeastOne,
                    Options = GuidedInstallerGroupDesignViewModel.GenerateOptions()
                },
            }
        };
    }
}
