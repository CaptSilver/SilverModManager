using SilverModManager.Sdk;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI;

/// <summary>
/// Tracks which "another app holds this scheme" notices the user has seen. The startup pass finds the holder before
/// the database, the CLI server or any window exists, so a message on the event bus alone would reach nobody. The
/// notice therefore stays pending, and a holder is saved as reported only when the consumer acknowledges it.
/// </summary>
/// <remarks>
/// A window picks up <see cref="Pending"/> when it opens, listens for <see cref="CliMessages.ProtocolHandlerHeldElsewhere"/>
/// after that, and calls <see cref="Acknowledge"/> for each notice it shows.
/// </remarks>
public sealed class ProtocolNotices(ISettingsManager settingsManager, IEventBus eventBus)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CliMessages.ProtocolHandlerHeldElsewhere> _pending = new();

    /// <summary>
    /// Notices published but not yet acknowledged, at most one per scheme.
    /// </summary>
    public IReadOnlyList<CliMessages.ProtocolHandlerHeldElsewhere> Pending
    {
        get
        {
            lock (_lock) return _pending.Values.ToArray();
        }
    }

    /// <summary>
    /// Publishes that <paramref name="holder"/> keeps <paramref name="scheme"/>, unless the user was already told
    /// about that holder. The holder is not saved as reported until <see cref="Acknowledge"/>.
    /// </summary>
    public void ReportHeldElsewhere(string scheme, string holder)
    {
        if (settingsManager.Get<ProtocolNoticeSettings>().LastReportedHolder.GetValueOrDefault(scheme) == holder) return;

        var notice = new CliMessages.ProtocolHandlerHeldElsewhere(scheme, holder);
        lock (_lock)
        {
            if (_pending.TryGetValue(scheme, out var existing) && existing == notice) return;
            _pending[scheme] = notice;
        }

        eventBus.Send(notice);
    }

    /// <summary>
    /// Records that the user was shown <paramref name="notice"/>, so later starts with the same holder stay quiet.
    /// </summary>
    public void Acknowledge(CliMessages.ProtocolHandlerHeldElsewhere notice)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(notice.Scheme, out var existing) && existing == notice)
                _pending.Remove(notice.Scheme);
        }

        settingsManager.Update<ProtocolNoticeSettings>(notices => notices with
        {
            LastReportedHolder = new Dictionary<string, string>(notices.LastReportedHolder) { [notice.Scheme] = notice.Holder },
        });
    }

    /// <summary>
    /// Records the outcome of a take of <paramref name="scheme"/>. When it registered, the remembered holder is
    /// dropped, and if another app held the scheme the user is told it was taken from it.
    /// </summary>
    public void Took(string scheme, UriSchemeRegistration result)
    {
        if (result.Registered is false) return;

        Forget(scheme);
        if (result.OtherHandler is { } previous)
            eventBus.Send(new CliMessages.ProtocolHandlerTaken(scheme, previous));
    }

    /// <summary>
    /// Drops everything remembered about <paramref name="scheme"/>. Call it when the app took the scheme, so a
    /// later holder is reported even if it is the one reported before.
    /// </summary>
    public void Forget(string scheme)
    {
        lock (_lock) _pending.Remove(scheme);

        if (settingsManager.Get<ProtocolNoticeSettings>().LastReportedHolder.ContainsKey(scheme) is false) return;

        settingsManager.Update<ProtocolNoticeSettings>(notices => notices with
        {
            LastReportedHolder = notices.LastReportedHolder.Where(pair => pair.Key != scheme).ToDictionary(pair => pair.Key, pair => pair.Value),
        });
    }
}
