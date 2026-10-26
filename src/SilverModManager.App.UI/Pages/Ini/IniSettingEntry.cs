using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Pages.Ini;

/// <summary>One catalog setting on a file's Settings tab, with the value the result has for it.</summary>
public sealed partial class IniSettingEntry : ReactiveObject
{
    public IniSettingEntry(IniSettingDefinition definition, string value, string? managedReason, Func<IniSettingEntry, Task> apply)
    {
        Definition = definition;
        Value = value;
        ManagedReason = managedReason ?? "";
        ApplyCommand = ReactiveCommand.CreateFromTask(() => apply(this), Observable.Return(managedReason is null));
    }

    public IniSettingDefinition Definition { get; }

    /// <summary>The value to write; starts as the result's value, empty when no file sets the key.</summary>
    [Reactive] public partial string Value { get; set; }

    /// <summary>Whether the app writes this key itself after the tweaks, so setting it here would change nothing.</summary>
    public bool IsManaged => ManagedReason != "";

    public string ManagedReason { get; }

    /// <summary>Writes <see cref="Value"/> into the loadout tweak. Unavailable for a managed key.</summary>
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
}
