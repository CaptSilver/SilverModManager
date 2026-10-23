using ReactiveUI;

namespace SilverModManager.UI.Sdk;

public interface IViewModel : IActivatableViewModel
{
    public Type ViewModelInterface { get; }
}
