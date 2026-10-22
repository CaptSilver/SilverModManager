using System.Diagnostics;
using NexusMods.App.UI.Controls;
using NexusMods.UI.Sdk;
using R3;
using Unit = R3.Unit;

namespace NexusMods.App.UI.Pages.Sorting;

/// <summary>
/// The on/off switch of a Load Order row. A locked row shows the switch but cannot be toggled.
/// </summary>
public sealed class LoadOrderToggleComponent : ReactiveR3Object, IItemModelComponent<LoadOrderToggleComponent>, IComparable<LoadOrderToggleComponent>
{
    public bool IsOn { get; }
    public bool IsLocked { get; }

    /// <summary>
    /// Explains why the switch is unavailable; null when it works.
    /// </summary>
    public string? ToolTip { get; }

    public ReactiveCommand<Unit> Toggle { get; }

    public LoadOrderToggleComponent(bool isOn, bool isLocked, string lockedToolTip)
    {
        IsOn = isOn;
        IsLocked = isLocked;
        ToolTip = isLocked ? lockedToolTip : null;
        Toggle = Observable.Return(!isLocked).ToReactiveCommand();
    }

    public int CompareTo(LoadOrderToggleComponent? other) => throw new UnreachableException("Data is sorted by the Adapter, not by the TreeDataGrid");

    private bool _isDisposed;
    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing) Toggle.Dispose();
            _isDisposed = true;
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// The flags of a Load Order row, with the items it needs loaded before it in the tooltip.
/// </summary>
public sealed class LoadOrderDetailsComponent : ReactiveR3Object, IItemModelComponent<LoadOrderDetailsComponent>, IComparable<LoadOrderDetailsComponent>
{
    public string Flags { get; }
    public string MastersToolTip { get; }

    public LoadOrderDetailsComponent(string? flags, string mastersToolTip)
    {
        Flags = flags ?? string.Empty;
        MastersToolTip = mastersToolTip;
    }

    public int CompareTo(LoadOrderDetailsComponent? other) => throw new UnreachableException("Data is sorted by the Adapter, not by the TreeDataGrid");
}
