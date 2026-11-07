using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    // The original's «Дублировать информационную базу» (Shift+Insert): the files of a file base go to a new folder,
    // the list gets a copy of the record right after the source. Nothing is overwritten: the folder must be new or empty.
    private async void DuplicateBase()
    {
        if (loadFailed || reading || bases.SelectedItem is not LibraryRow { Entry: { } entry } || ListLocked() || CommonLocked(entry)) return;
        if (entry.Kind != ConnectionKind.File) { status.Text = "Дублировать можно только файловую базу: серверную копируют средствами кластера 1С."; return; }
        var dialog = new Window { Title = "Дублирование информационной базы", Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = new TextBox { Text = BaseDuplicate.SuggestName(entry.Name, library.Bases.Select(x => x.Name)) };
        AutomationProperties.SetName(name, "Наименование новой базы");
        string suggested;
        try { suggested = BaseDuplicate.SuggestFolder(entry.Location); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { suggested = ""; }
        var folder = new TextBox { Text = suggested };
        AutomationProperties.SetName(folder, "Каталог новой базы");
        var error = new TextBlock { Foreground = UiColors.Error, TextWrapping = TextWrapping.Wrap };
        var browse = SettingsWindow.Command("Выбрать каталог…", async () =>
        {
            // A failing picker (the storage provider, the portal on Linux) is said in the window, which stays usable (Codex full audit 24.10, P2-7).
            try
            {
                var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Каталог новой базы", AllowMultiple = false });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) folder.Text = path;
            }
            catch (Exception ex) { AppLog.Warning("base.duplicate.picker_failed", ex); error.Text = "Каталог не выбран: " + LegacyImport.ErrorMessage(ex); }
        });
        browse.HorizontalAlignment = HorizontalAlignment.Left;
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, IsVisible = false, Height = 8 };
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Muted };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(DialogLayout.Field("Исходная база:", DialogLayout.Label(entry.Name + "\n" + entry.Location)));
        panel.Children.Add(DialogLayout.Field("Наименование:", name));
        panel.Children.Add(DialogLayout.Field("Каталог новой базы:", folder));
        panel.Children.Add(browse);
        panel.Children.Add(DialogLayout.Label("Файлы базы будут скопированы в новый или пустой каталог; в список добавится копия записи со всеми " +
            "параметрами. Перед копированием завершите все сеансы 1С с этой базой."));
        var buttons = new WrapPanel();
        CancellationTokenSource? copying = null;
        var done = false;
        dialog.Closing += (_, e) => { if (copying is not null && !done) { e.Cancel = true; copying.Cancel(); } };
        var start = SettingsWindow.Command("Дублировать", async () =>
        {
            if (copying is not null) return;
            error.Text = "";
            var target = folder.Text?.Trim() ?? "";
            BaseEntry clone;
            try
            {
                BaseDuplicate.CheckTarget(entry.Location, target);
                clone = BaseDuplicate.Clone(entry, name.Text ?? "", target);
                clone.Validate();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            { error.Text = ex.Message; return; }
            copying = new CancellationTokenSource();
            name.IsEnabled = folder.IsEnabled = browse.IsEnabled = false;
            foreach (var button in buttons.Children.OfType<Button>()) button.IsEnabled = button.IsCancel;
            progress.IsVisible = true; state.Text = "Подсчёт размера…";
            AppLog.Info("base.duplicate.begin");
            try
            {
                var source = entry.Location; var token = copying.Token;
                var total = await Task.Run(() => BaseDuplicate.Measure(source), token);
                progress.Maximum = Math.Max(1, total);
                var report = new Progress<long>(copied => { progress.Value = copied; state.Text = $"Скопировано {FormatSize(copied)} из {FormatSize(total)}"; });
                await Task.Run(() => BaseDuplicate.CopyFiles(source, target, report, token), token);
                state.Text = "Файлы скопированы. Сохранение списка…";
                var saved = await Persist(library with { Bases = [.. library.Bases, clone] }, copies: new Dictionary<Guid, Guid> { [clone.Id] = entry.Id });
                // The shared list may be saved while the local profile is not: the record exists then, only the settings wait.
                if (saved || library.Bases.Any(x => x.Id == clone.Id))
                {
                    AppLog.Info(saved ? "base.duplicate.done" : "base.duplicate.done.profile_pending");
                    var pending = saved ? "" : " " + status.Text;
                    done = true; search.Text = ""; RevealSelected(clone.Id.ToString()); Refresh(clone.Id); dialog.Close();
                    status.Text = $"База продублирована: {clone.Name} ({clone.Location}).{pending}";
                    return;
                }
                // The files are already in place: keep them and say where, the record can be added by hand.
                done = true;
                error.Text = $"Файлы скопированы в {clone.Location}, но запись не добавлена в список: {status.Text} Добавьте базу через «Добавить».";
                state.Text = ""; progress.IsVisible = false;
                foreach (var button in buttons.Children.OfType<Button>()) button.IsEnabled = button.IsCancel;
            }
            catch (OperationCanceledException)
            {
                AppLog.Info("base.duplicate.cancelled");
                done = true; dialog.Close(); status.Text = "Дублирование отменено; скопированные файлы удалены.";
            }
            catch (IncompleteCopyException ex)
            {
                // The partial copy is still on the disk: say where instead of claiming it was removed.
                AppLog.Warning("base.duplicate.leftover", ex);
                done = ex.Cancelled; copying = ex.Cancelled ? copying : null; progress.IsVisible = false; state.Text = "";
                var reason = ex.Cancelled ? "Дублирование отменено." : "Копирование не выполнено: " + LegacyImport.ErrorMessage(ex.Reason);
                error.Text = $"{reason} Незавершённая копия не удалена: {ex.Leftover}. Удалите этот каталог вручную.";
                name.IsEnabled = folder.IsEnabled = browse.IsEnabled = !ex.Cancelled;
                foreach (var button in buttons.Children.OfType<Button>()) button.IsEnabled = !ex.Cancelled || button.IsCancel;
                status.Text = error.Text;
            }
            catch (Exception ex)
            {
                AppLog.Warning("base.duplicate.failed", ex);
                copying = null; progress.IsVisible = false; state.Text = "";
                error.Text = LegacyImport.ErrorMessage(ex);
                name.IsEnabled = folder.IsEnabled = browse.IsEnabled = true;
                foreach (var button in buttons.Children.OfType<Button>()) button.IsEnabled = true;
            }
        });
        start.IsDefault = true;
        var cancel = SettingsWindow.Command("Отмена", () => { if (copying is not null && !done) copying.Cancel(); else dialog.Close(); });
        cancel.IsCancel = true;
        buttons.Children.Add(start); buttons.Children.Add(cancel);
        panel.Children.Add(progress); panel.Children.Add(state);
        dialog.Content = DialogLayout.Frame(panel, buttons, error);
        typography.ApplyDialog(dialog); dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        await dialog.ShowDialog(this);
        copying?.Dispose();
        RestoreListFocus();
    }
}
