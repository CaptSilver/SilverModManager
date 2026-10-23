namespace SilverModManager.MnemonicDB.Abstractions;

public record ModelDefinition(string Name, IAttribute PrimaryAttribute, IAttribute[] AllAttributes);
