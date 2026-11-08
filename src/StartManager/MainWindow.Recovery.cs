using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    private string recoveryDirectory;
    private string? recoveryNotice;
    private string? updateProgressNotice;
    // The notice of the update of this start. The passwords of an earlier build removed by it get a message of their own
    // (ShowPasswordsNotice): in the status line they were cut at the usual width (final Linux run, О1).
    private string? updateNotice = Program.UpdateNotice;

    /// <summary>Once, after the first load of the list (the typography of the profile is known by then): the message about the
    /// passwords of an earlier build removed at this start. Acknowledged only after the message was closed; a message that could
    /// not be shown, or a window closed meanwhile, keeps the notice for the next start.</summary>
    private async Task ShowPasswordsNotice()
    {
        if (Program.PasswordsNotice is not { } text || closed) return;
        try { await BackupDialogs.Inform(this, typography, Program.MessageTitle, text); }
        catch (Exception ex) { AppLog.Warning("passwords.notice.show_failed", ex); return; }
        if (!closed) await Program.PasswordsNoticeShown();
    }

    /// <summary>Automatic update at startup (Настройки → Автообновление); failures are logged, said in the status line and never block the list.</summary>
    private async Task CheckUpdatesAsync(UpdateMode mode, string repository, int keep)
    {
        if (mode == UpdateMode.Off) return;
        try
        {
            var current = Program.CurrentVersion;
            ReleaseInfo? offer = null;
            // «Загружать автоматически»: no window; the status line shows the percent while the list is idle.
            var finished = false;
            var progress = new Progress<UpdateProgress>(p => { if (!finished) ShowBackgroundUpdate(p.Percent); });
            string? Check()
            {
                var updater = Program.CreateUpdater(repository, keep);
                if (updater.FromPrevious || updater.PendingVersion is not null || Updater.ChangedInThisProcess) return null;
                var latest = updater.Latest();
                if (latest.Version <= current) return null;
                if (mode == UpdateMode.Notify) { offer = latest; return $"Доступна версия {latest.Version}: Настройки → Автообновление"; }
                // Prepared now, applied by a helper after the program exits: the running file is never replaced.
                try { return updater.StageAndLaunch(latest, current, Updater.CurrentRuntime, Program.LightBuild, Program.RestartArguments, progress).Notice; }
                finally { finished = true; }
            }
            string? notice;
            try { notice = await Task.Run(Check); }
            finally { ShowBackgroundUpdate(null); }
            AppLog.Info(notice is null ? "update.auto.current" : "update.auto.found");
            if (notice is null || closed) return;
            updateNotice = notice;
            if (offer is not null) { offeredRelease = offer; offeredRepository = repository; }
            // Published when the program is idle (the list may still be read: Grok audit 15.10). «Уведомлять»: the original's
            // FormUpdateInfo comes up by itself once a session; the notice opens it again.
            pendingUpdate.Found(offer is not null && UpdateOffer.ShowsItself(mode));
            PublishPendingUpdate();
        }
        catch (Exception ex)
        {
            AppLog.Warning("update.auto.failed", ex);
            if (closed) return;
            // Said in the status line once the program is idle, after a notice of the last update if there is one.
            updateNotice = (updateNotice is { Length: > 0 } earlier ? earlier + " · " : "") + UpdateOffer.CheckFailed(ex);
            pendingUpdate.Found(offers: false);
            PublishPendingUpdate();
        }
    }

    /// <summary>The progress of the background download in the status line: shown only over the idle description (not over the
    /// message of another operation), removed with null.</summary>
    private void ShowBackgroundUpdate(string? text)
    {
        if (closed) return;
        var idle = !reading && !writing && status.Text == SourceDescription();
        updateProgressNotice = text;
        if (idle) status.Text = SourceDescription();
    }

    private async Task<bool> ResolvePendingProfile(bool closing = false)
    {
        if (!profilePending) return true;
        if (reading || closed) return false;
        if (await SavePendingProfile()) return true;
        if (closed) return false;

        var dialog = new Window
        {
            Title = "Несохранённые локальные настройки", Width = 640, Height = 400,
            MinWidth = 420, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = status.Text, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock
        {
            Text = "Можно сохранить отдельную копию текущих настроек. Профиль на диске не будет перезаписан. " +
                (closing ? "После записи копии программа закроется." : "После записи копии будут загружены настройки с диска. Повторите нужную команду после обновления."),
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock { Text = "Каталог копии: " + recoveryDirectory, TextWrapping = TextWrapping.Wrap });
        var error = new TextBlock { Foreground = UiColors.Error, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(error);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var saving = false;
        var confirming = false;
        dialog.Closing += (_, e) => { if (saving || confirming) e.Cancel = true; };
        var save = Action("Сохранить копию и продолжить", async () =>
        {
            if (saving) return;
            saving = true; buttons.IsEnabled = false;
            var failedBefore = loadFailed;
            var saved = false;
            BeginWriting("Сохранение отдельной копии настроек…");
            try
            {
                var pending = library;
                var path = Path.Combine(recoveryDirectory, $"library-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
                await Task.Run(() =>
                {
                    if (File.Exists(recoveryDirectory))
                        throw new IOException("Путь каталога копий занят файлом. Проверьте указанный каталог.");
                    if (OperatingSystem.IsWindows()) Directory.CreateDirectory(recoveryDirectory);
                    else
                    {
                        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                        Directory.CreateDirectory(recoveryDirectory, mode);
                        File.SetUnixFileMode(recoveryDirectory, mode);
                    }
                    new LibraryStore(path).Save(pending);
                });
                recoveryNotice = "Несохранённые настройки сохранены отдельно: " + path;
                profilePending = false;
                saved = true;
            }
            catch (Exception ex)
            {
                error.Text = "Копия не сохранена: " + LegacyImport.ErrorMessage(ex);
                status.Text = error.Text + " Локальные настройки остаются в памяти.";
            }
            finally
            {
                EndWriting(failedBefore); reading = true;
                saving = false; buttons.IsEnabled = true;
            }
            if (saved) dialog.Close(true);
        });
        buttons.Children.Add(save);
        var cancel = Action("Отмена", () => dialog.Close(false)); cancel.IsCancel = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(Action("Продолжить без сохранения", async () =>
        {
            if (saving || confirming) return;
            confirming = true; buttons.IsEnabled = false;
            bool discard;
            try { discard = await ConfirmDiscardPending(dialog); }
            finally { confirming = false; buttons.IsEnabled = true; }
            if (!discard) return;
            profilePending = false;
            recoveryNotice = "Несохранённые локальные параметры отменены по вашему выбору.";
            dialog.Close(true);
        }));
        dialog.Content = DialogLayout.Frame(body, buttons);
        typography.ApplyDialog(dialog);
        reading = true;
        bool recovered;
        try { recovered = await dialog.ShowDialog<bool>(this); }
        finally { reading = false; }
        if (!recovered || closed) return false;
        status.Text = recoveryNotice;
        ToolTip.SetTip(status, recoveryNotice);
        if (closing) return true;
        // Start with a fresh observation of the current profile. Never reuse the
        // stale store or overwrite another instance's changes with the saved copy.
        await ReloadAsync();
        return false;
    }

    /// <summary>The list was left by a write that never finished. Editing stays off until the user picks: the text from before
    /// the write (.bak; the file as it is now goes to a snapshot first) or the file as it is. Cancel changes nothing on disk.</summary>
    private async Task OfferInterruptedRecovery(InterruptedWriteException interrupted, bool reload)
    {
        if (closed) return;
        // Neither choice can be carried out on a list that cannot be written: the reason is shown instead of the offer.
        var path = interrupted.PathName;
        if (await Task.Run(() => ListAccess.ReadOnlyReason(path)) is { } reason)
        {
            if (!closed) status.Text = interrupted.Message + " " + new ListReadOnlyException(reason, ListReadOnlyException.RecoveryEffect).Message;
            return;
        }
        if (closed) return;
        var dialog = new Window { Title = "Прерванная запись списка", Width = 640, SizeToContent = SizeToContent.Height, MinWidth = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = interrupted.Message, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(DialogLayout.Label("Файл: " + interrupted.PathName));
        body.Children.Add(DialogLayout.Label(interrupted.BackupMatches
            ? "«Восстановить из .bak» вернёт список, каким он был до этой записи; последнее изменение придётся повторить. Текущий текст файла сначала сохранится снимком."
            : "Восстановить автоматически нельзя. «Оставить как есть» снимет отметку. Если список после этого не прочитается, исправьте файл в текстовом редакторе или замените его исправной копией: снимки списка лежат в папке Snapshots рядом с настройками."));
        body.Children.Add(DialogLayout.Label("«Оставить как есть» — если файл уже исправлен вручную или другой программой: список будет прочитан в текущем виде."));
        // On a slow share the list may be read while another window is still writing it: the same marker, but no failure.
        body.Children.Add(DialogLayout.Label("Если этот список сейчас сохраняет другое окно БазоНавигатора, нажмите «Отмена» и через несколько секунд — «Перечитать список баз»."));
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        if (interrupted.BackupMatches) buttons.Children.Add(Action("Восстановить из .bak", () => dialog.Close("restore")));
        buttons.Children.Add(Action("Оставить как есть", () => dialog.Close("keep")));
        var cancel = Action("Отмена", () => dialog.Close(null)); cancel.IsCancel = true; buttons.Children.Add(cancel);
        dialog.Content = DialogLayout.Frame(body, buttons);
        typography.ApplyDialog(dialog); dialog.Opened += (_, _) => cancel.Focus();
        string? choice;
        reading = true;
        try { choice = await dialog.ShowDialog<string?>(this); }
        finally { reading = false; }
        if (choice is null || closed) return;
        var seen = interrupted.Current; var folder = Snapshots; var failedBefore = loadFailed;
        BeginWriting(choice == "restore" ? "Восстановление списка из .bak…" : "Снятие отметки прерванной записи…");
        try
        {
            await Task.Run(() =>
            {
                if (choice == "keep") { V8iStore.DismissInterruptedWrite(path, seen); return; }
                folder.Create(path, seen, "Перед восстановлением после прерванной записи");
                V8iStore.RecoverInterruptedWrite(path, seen);
            });
            AppLog.Info(choice == "restore" ? "list.interrupted.restored" : "list.interrupted.kept");
        }
        catch (Exception ex)
        {
            AppLog.Warning("list.interrupted.failed", ex);
            // The open list still carries the marker: editing waits for a new load. A list that was never opened keeps the state.
            EndWriting(reload || failedBefore);
            status.Text = "Список не восстановлен: " + LegacyImport.ErrorMessage(ex) +
                (reload || failedBefore ? " Редактирование отключено; нажмите «Перечитать список баз»." : "");
            return;
        }
        EndWriting(failedBefore);
        if (reload) await ReloadAsync();
        if (closed) return;
        var done = choice == "restore" ? "Список восстановлен из .bak; текст прерванной записи — в снимках." : "Отметка прерванной записи снята.";
        // A list that still does not read keeps the reason of the failed load next to what was done.
        status.Text = done + (!reload ? " Откройте список снова." : loadFailed ? " " + status.Text : " " + SourceDescription());
    }

    private Task<bool> ConfirmDiscardPending(Window owner)
    {
        var confirmation = new Window
        {
            Title = "Отменить несохранённые изменения?", Width = 560, Height = 280,
            MinWidth = 400, MinHeight = 250, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var body = new TextBlock
            {
                Text = "Несохранённые локальные параметры, включая индивидуальный клиент и выбор платформы, будут потеряны. " +
                    "Изменения общего списка .v8i уже записаны и не отменятся. Профиль другого экземпляра не изменится.",
                TextWrapping = TextWrapping.Wrap
            };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var cancel = Action("Вернуться", () => confirmation.Close(false)); cancel.IsCancel = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(Action("Отменить несохранённые изменения", () => confirmation.Close(true)));
        confirmation.Content = DialogLayout.Frame(body, buttons);
        typography.ApplyDialog(confirmation);
        return confirmation.ShowDialog<bool>(owner);
    }
}
