namespace StartManager.Core;

/// <summary>The texts of «Обновление БазоНавигатора» (the original's FormUpdateInfo) and when it is offered.</summary>
public static class UpdateOffer
{
    public const string Title = "Обновление БазоНавигатора";
    public const string Heading = "Доступна новая версия БазоНавигатора";
    public const string NoNotes = "Описание выпуска не найдено.";

    public static string Message(Version latest, Version current) =>
        $"Доступна версия {latest} (сейчас установлена {current}). Хотите загрузить новую версию?";

    /// <summary>The release description (body of the GitHub release) with unified line breaks; an empty one is named.</summary>
    public static string Notes(string? notes)
    {
        var text = (notes ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return text.Length > 0 ? text : NoNotes;
    }

    /// <summary>The window comes up by itself only after the startup check of the «уведомлять» way (once a session, see
    /// PendingUpdateNotice); the automatic download keeps to the status bar, and the notice can always open the window again.</summary>
    public static bool ShowsItself(UpdateMode mode) => mode == UpdateMode.Notify;

    /// <summary>The status line of an automatic check that failed (Grok full audit 24.10, P2-3): the text «Проверить сейчас» shows
    /// (gh failures are already put in words without what gh printed about the token), and no window: the check runs at every start.</summary>
    public static string CheckFailed(Exception error) => "Автоматическая проверка обновлений не выполнена: " + LegacyImport.ErrorMessage(error);
}

/// <summary>
/// The result of the startup check waiting to be shown. The check may finish while the list is read or written, or while
/// another window of the program is open: the notice is published, and the offer comes up by itself, once that ends —
/// once a session, never after «Напомнить позже» or after the user opened the offer from the notice.
/// </summary>
public sealed class PendingUpdateNotice
{
    private bool noticePending, offerPending;

    /// <summary>The check found a release: notice — the status bar text; offers — the offer is to come up by itself.</summary>
    public void Found(bool offers) { noticePending = true; offerPending |= offers; }

    /// <summary>What to do now: publish the notice when the program is idle, show the offer when also no other window is open.</summary>
    public (bool PublishNotice, bool ShowOffer) Take(bool busy, bool otherWindowOpen)
    {
        if (busy) return (false, false);
        var publish = noticePending; noticePending = false;
        var show = offerPending && !otherWindowOpen;
        if (show) offerPending = false;
        return (publish, show);
    }

    /// <summary>«Напомнить позже», or the offer opened from the notice: it does not come up by itself any more.</summary>
    public void Settled() => offerPending = false;

    public bool Waiting => noticePending || offerPending;
}
