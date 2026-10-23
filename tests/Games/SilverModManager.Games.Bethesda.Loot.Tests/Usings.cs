global using Xunit;
global using FluentAssertions;
// System.IO.File would otherwise be ambiguous with the metadata type through the implicit usings.
global using File = SilverModManager.Games.Bethesda.Loot.Metadata.File;
