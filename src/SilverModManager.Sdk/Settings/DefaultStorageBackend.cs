using JetBrains.Annotations;

namespace SilverModManager.Sdk.Settings;

[PublicAPI]
public record DefaultStorageBackend(IBaseStorageBackend Backend);
