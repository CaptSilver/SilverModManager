using TransparentValueObjects;

namespace SilverModManager.App.UI.WorkspaceSystem;

[ValueObject<Guid>]
public readonly partial struct PageFactoryId : IAugmentWith<JsonAugment>;
