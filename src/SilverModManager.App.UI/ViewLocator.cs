using Avalonia.Controls;
using Avalonia.Controls.Templates;
using SilverModManager.Sdk.Reflection;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI;

public class ViewLocator : IDataTemplate
{
    public Control Build(object? data)
    {
        if (data is null)
            throw new InvalidOperationException("ViewLocator.Build was called without a view model");

        var name = data.GetType().RequiredFullName().Replace("ViewModel", "View");
        var type = Type.GetType(name);

        if (type != null)
        {
            return type.CreateRequiredInstance<Control>();
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is IViewModel;
    }
}
