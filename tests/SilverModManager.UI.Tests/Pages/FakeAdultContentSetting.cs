using System.Reactive.Subjects;
using SilverModManager.App.UI.Settings;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>An adult content setting a test flips directly, without going through the settings file.</summary>
internal sealed class FakeAdultContentSetting(bool initial = false) : IAdultContentSetting
{
    private readonly BehaviorSubject<bool> _value = new(initial);

    public bool ShowAdultContent
    {
        get => _value.Value;
        set => _value.OnNext(value);
    }

    public IObservable<bool> Changes => _value;
}
