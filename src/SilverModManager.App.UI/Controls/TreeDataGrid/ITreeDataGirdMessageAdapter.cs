using R3;

namespace SilverModManager.App.UI.Controls;

public interface ITreeDataGirdMessageAdapter<TMessage>
    where TMessage : notnull
{
    Subject<TMessage> MessageSubject { get; }
}
