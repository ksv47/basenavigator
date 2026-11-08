using Avalonia.Controls;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    // A dump or a load of 1C may take hours on a large base; «Отмена» of the window of progress stops it sooner.
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromHours(6);

    // A backup command of «Свойства ▾» is under way (reading its data, its window open, its run): a second one waits. The operation
    // lease is taken only by the run itself, so without this a key pressed again during the reads opened a second window (Grok audit,
    // part 4).
    private bool backupCommandOpen;

    // The base of a backup command of «Свойства ▾» (and its keys): a file or a server base, no other backup or load running.
    private BaseEntry? BackupBase()
    {
        if (loadFailed || reading || closed) { if (!closed) status.Text = MainCommands.BusyText; return null; }
        if (bases.SelectedItem is not LibraryRow { Orphan: null, Entry: { } entry }) { status.Text = MainCommands.SelectBaseText; return null; }
        if (!BackupCommands.Applies(entry)) { status.Text = MainCommands.BackupKindText; return null; }
        if (BackupOperation.Running || backupCommandOpen) { status.Text = BackupOperation.BusyMessage; return null; }
        return entry;
    }

    // The login the fields start with: the designer login of the page «Аутентификация» with its saved password.
    private async Task<(string User, string Password)> BackupLogin(BaseEntry entry)
    {
        var saved = await BackupPasswords(entry);
        return BackupForms.Prefill(entry, role => saved?.GetValueOrDefault(role));
    }

    // The saved passwords of the base in its list; an unreadable vault gives none and is logged.
    private async Task<IReadOnlyDictionary<PasswordRole, string>?> BackupPasswords(BaseEntry entry)
    {
        var vault = new PasswordVault(store.PathName);
        var snapshot = library; var source = SourceOf(entry);
        try
        {
            var read = await Task.Run(() => vault.Read(snapshot, source, entry.Id));
            return read.Passwords;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Warning("passwords.read.failed", ex);
            return null;
        }
    }

    // The thick client of the base's platform for DT and CF; the installations are searched off the UI thread.
    private async Task<string?> BackupClient(Window owner, BaseEntry entry, string title)
    {
        var snapshot = library;
        try { return await Task.Run(() => PlatformCatalog.Resolve(snapshot, entry, FindInstallations(snapshot), designer: true)); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            await BackupDialogs.Inform(owner, typography, title, "Клиент 1С не найден: " + ex.Message);
            return null;
        }
    }

    // The original's «Выгрузить резервную копию данных» (actV8Backup, Shift+Ctrl+F5): «Выполнение сценария».
    private async void BackupSelected()
    {
        if (BackupBase() is not { } entry) return;
        backupCommandOpen = true;
        try
        {
            BackupScenarioSet scenarios;
            try { scenarios = await Task.Run(new BackupScenarioStore(store.PathName).Load); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AppLog.Warning("backup.scenarios.read_failed", ex);
                await BackupDialogs.Inform(this, typography, BackupExecuteWindow.Caption, "Сценарии резервирования не прочитаны: " + LegacyImport.ErrorMessage(ex));
                RestoreListFocus(); return;
            }
            var login = await BackupLogin(entry);
            if (closed) return;
            var journal = new BackupJournalStore(store.PathName);
            var window = new BackupExecuteWindow(entry, scenarios, library.BackupFolder, login, (owner, request) => ExecuteBackup(owner, request, journal), typography);
            await window.ShowDialog(this);
            if (window.KeptLock is { } kept && !closed) status.Text = kept;
            RestoreListFocus();
        }
        finally { backupCommandOpen = false; }
    }

    private async Task<bool> ExecuteBackup(Window owner, BackupRequest request, BackupJournalStore journal)
    {
        const string title = BackupExecuteWindow.Caption;
        if (BackupOperation.Running) { await BackupDialogs.Inform(owner, typography, title, BackupOperation.BusyMessage); return false; }
        if (request.Scenario.Format != BackupFormat.Zip)
        {
            if (await BackupClient(owner, request.Base, title) is not { } client) return false;
            request = request with { Client = client };
        }
        AppLog.Info("backup.begin");
        BackupResult? result;
        try
        {
            result = await BackupFlow.Run(request, text => BackupDialogs.Ask(owner, typography, title, text),
                plan => BackupProgressWindow.Run(owner, typography, "Выгрузка данных", (progress, cancel) =>
                    Task.Run(() => BackupRun.Run(plan, journal, progress.Report, BackupTimeout, cancel), CancellationToken.None)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            AppLog.Error("backup.failed", ex);
            result = new BackupResult(false, "Выгрузка не выполнена: " + LegacyImport.ErrorMessage(ex));
        }
        if (result is null) { AppLog.Info("backup.declined"); return false; }
        AppLog.Info(result.Success ? "backup.done" : "backup.not_done");
        if (!closed) status.Text = result.Message;
        await BackupDialogs.Inform(owner, typography, title, BackupForms.Report(result));
        return result.Success;
    }

    // The original's «Загрузить данные из резервной копии» (actV8Restore, Shift+Ctrl+F7): «Список выгрузок», then the load.
    private async void ShowBackupList()
    {
        if (BackupBase() is not { } entry) return;
        backupCommandOpen = true;
        try
        {
            var journal = new BackupJournalStore(store.PathName);
            BackupJournal records;
            try { records = await Task.Run(journal.Load); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AppLog.Warning("backup.journal.read_failed", ex);
                await BackupDialogs.Inform(this, typography, BackupListWindow.Caption, "Журнал выгрузок не прочитан: " + LegacyImport.ErrorMessage(ex));
                RestoreListFocus(); return;
            }
            if (closed) return;
            // The original opens the list and closes it at once with this message when the tree is empty.
            if (BackupList.Groups(records, entry.Id).Count == 0)
            {
                await BackupDialogs.Inform(this, typography, BackupListWindow.Caption, BackupList.Empty);
                RestoreListFocus(); return;
            }
            var choice = await BackupListWindow.Show(this, typography, entry, journal, records);
            if (choice is null || closed) { RestoreListFocus(); return; }
            var saved = await BackupPasswords(entry);
            var credentials = BackupCredentials.FromBase(entry, role => saved?.GetValueOrDefault(role));
            await Restore(entry, choice.File, credentials, BackupList.Confirmation(entry.Name, choice.File), journal);
        }
        finally { backupCommandOpen = false; }
    }

    // The original's «Загрузить информационную базу из файла (*.dt)» and «Загрузить конфигурацию из файла (*.cf)»: «Загрузка данных».
    private async void LoadSelected(BackupFormat format)
    {
        if (BackupBase() is not { } entry) return;
        backupCommandOpen = true;
        try
        {
            var login = await BackupLogin(entry);
            if (closed) return;
            var choice = await BackupLoadWindow.Show(this, typography, entry, format, login);
            if (choice is null || closed) { RestoreListFocus(); return; }
            // The window warned already (its red line); a server base is still warned that nothing undoes the load.
            await Restore(entry, choice.File, BackupForms.Credentials(entry, choice.User, choice.Password), null, new BackupJournalStore(store.PathName));
        }
        finally { backupCommandOpen = false; }
    }

    // The load shared by the list and «Загрузка данных»: the questions, the window of progress and the result.
    private async Task Restore(BaseEntry entry, string file, BackupCredentials credentials, string? confirmation, BackupJournalStore journal)
    {
        const string title = BackupLoadWindow.Caption;
        try
        {
            if (BackupOperation.Running) { await BackupDialogs.Inform(this, typography, title, BackupOperation.BusyMessage); return; }
            var request = new RestoreRequest { Base = entry, File = file, Credentials = credentials, Journal = journal };
            if (BackupRestore.FormatOf(file) is BackupFormat.Dt or BackupFormat.Cf)
            {
                if (await BackupClient(this, entry, title) is not { } client) return;
                request = request with { Client = client };
            }
            AppLog.Info("restore.begin");
            RestoreResult? result;
            try
            {
                result = await RestoreFlow.Run(request, confirmation, text => BackupDialogs.Ask(this, typography, title, text),
                    plan => BackupProgressWindow.Run(this, typography, title, (progress, cancel) =>
                        Task.Run(() => BackupRestore.Run(plan, progress.Report, BackupTimeout, cancel), CancellationToken.None)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                AppLog.Error("restore.failed", ex);
                result = new RestoreResult(false, "Загрузка не выполнена: " + LegacyImport.ErrorMessage(ex));
            }
            if (result is null) { AppLog.Info("restore.declined"); return; }
            AppLog.Info(result.Success ? "restore.done" : "restore.not_done");
            if (!closed) status.Text = result.Message;
            await BackupDialogs.Inform(this, typography, title, result.Message);
        }
        finally { RestoreListFocus(); }
    }
}
