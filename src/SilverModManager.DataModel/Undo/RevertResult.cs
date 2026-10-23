using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.DataModel.Undo;

/// <summary>
/// What <see cref="UndoService.RevertTo"/> left behind after committing.
/// </summary>
/// <param name="DanglingReferences">
/// References the revert restored whose target no longer exists, as (entity holding the reference,
/// attribute, missing target).
/// </param>
/// <param name="Residual">
/// Entity/attribute pairs where the loadout's owned entities still differ from the revision after the
/// commit. Anything in here means the revert did not do what it set out to do.
/// </param>
public record RevertResult(
    IReadOnlyList<(EntityId Entity, AttributeId Attribute, EntityId Target)> DanglingReferences,
    IReadOnlyList<(EntityId Entity, AttributeId Attribute)> Residual);
