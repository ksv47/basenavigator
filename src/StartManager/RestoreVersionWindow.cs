using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StartManager.Core;

namespace StartManager;

/// <summary>
/// The original's «Восстановление версии» (smFormRollBack): the saved versions in a grid filling the window — «Версия» and the
/// folder of the copy (the original shows the file), here also the build variant; a double click, Enter or Ctrl+Enter restores;
/// «Закрыть» on the left, «Восстановить» (the default) after it. The versions come from the archive next to the program
/// (Updater.SavedVersions), opened from «Сервис ▾» and from «Настройки → Автообновление».
/// </summary>
internal static class RestoreVersionWindow
{
    public const string Title = "Восстановление версии";

    /// <summary>Reads the archive without blocking the window, shows the list and asks the original's questions; returns the
    /// version the user confirmed, null when the window was closed.</summary>
    public static async Task<SavedVersion?> Choose(Window owner, UiTypography typography, Func<Updater> create)
    {
        IReadOnlyList<SavedVersion> versions = [];
        string? note = null;
        var fromSavedCopy = false;
        var archive = "";
        try
        {
            (versions, fromSavedCopy, archive) = await Task.Run(() =>
            {
                var updater = create();
                return (updater.SavedVersions(), updater.FromPrevious, updater.ArchivePath);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warning("update.archive.read_failed", ex);
            note = "Не удалось прочитать архив прежних версий: " + LegacyImport.ErrorMessage(ex);
        }
        var locked = Updater.ChangedInThisProcess;
        if (locked)
            note = "Файлы программы уже изменены в этом сеансе (подготовлено обновление или восстановление). " +
                "Перезапустите программу, чтобы восстановить другую версию.";
        else if (note is null && versions.Count == 0)
            note = "Архив прежних версий пуст. Версии сохраняются в нём при установке обновлений и при восстановлении" +
                (archive.Length > 0 ? $" (папка {archive})." : ".");
        return await Show(owner, typography, versions, Program.CurrentVersion, Program.LightBuild, fromSavedCopy, note, !locked && note is null);
    }

    private static async Task<SavedVersion?> Show(Window owner, UiTypography typography, IReadOnlyList<SavedVersion> versions, Version current,
        bool currentLight, bool fromSavedCopy, string? note, bool allowed)
    {
        var dialog = new Window { Title = Title, Width = 587, Height = 421, MinWidth = 380, MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        double TextWidth(string text) => Math.Ceiling(UiTypography.Measure(text, typography.DialogFamily, typography.DialogSize).Width);
        // The headers are semibold: a tenth wider than their regular measure.
        var versionWidth = Math.Max(TextWidth("Версия") * 1.1, versions.Count == 0 ? 0 : versions.Max(x => TextWidth(x.Version.ToString()))) + 16;
        // A copy a program runs from is marked in the variant column (it is restorable: only replacing it waits).
        string VariantText(SavedVersion x) => x.InUse ? x.Variant + ", запущена" : x.Variant;
        var variantWidth = Math.Max(TextWidth("Вариант") * 1.1, versions.Select(x => TextWidth(VariantText(x))).Append(TextWidth("автономный")).Max()) + 16;
        // Whole pixels, written without the culture: «95,7» would read as two columns.
        var columns = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Math.Ceiling(Math.Max(90, versionWidth))},*,{Math.Ceiling(variantWidth)}");
        var cells = new List<TextBlock>();
        Grid Row(string version, string folder, string variant, bool header)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
            foreach (var (text, column) in new[] { (version, 0), (folder, 1), (variant, 2) })
            {
                var cell = new TextBlock { Text = text, Margin = new Thickness(5, 3), TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = column == 1 ? TextTrimming.PathSegmentEllipsis : TextTrimming.CharacterEllipsis,
                    FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal, Foreground = header ? UiColors.Muted : UiColors.Text };
                if (!header && text.Length > 0) ToolTip.SetTip(cell, text);
                // The original's grid lines between the columns.
                var line = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 0, column < 2 ? 1 : 0, 0), Child = cell };
                Grid.SetColumn(line, column); row.Children.Add(line); cells.Add(cell);
            }
            return row;
        }
        var header = Row("Версия", "Папка", "Вариант", true);
        var headerFrame = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1, 1, 1, 0), Background = UiColors.Surface, Child = header };
        var list = new ListBox { Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
            new Setter(ListBoxItem.MinHeightProperty, 0d) } });
        AutomationProperties.SetName(list, "Сохранённые версии");
        // Rows take the width of the list (paths shorten in the middle) instead of scrolling sideways under a fixed header.
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        list.ItemsSource = versions.Select(x =>
        {
            var row = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = Row(x.Version.ToString(), x.Folder, VariantText(x), false) };
            AutomationProperties.SetName(row, $"{x.Version} · {VariantText(x)}");
            return row;
        }).ToList();
        if (versions.Count > 0) list.SelectedIndex = 0;
        var body = new Grid { RowDefinitions = new("Auto,*") };
        body.Children.Add(headerFrame); Grid.SetRow(list, 1); body.Children.Add(list);
        // The header keeps the width of the rows: a vertical scroll bar of the list narrows both (Grok audit 15.10).
        headerFrame.HorizontalAlignment = HorizontalAlignment.Left;
        list.LayoutUpdated += (_, _) =>
        {
            if (list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll) return;
            var width = scroll.Viewport.Width + 2;
            if (width > 2 && (double.IsNaN(headerFrame.Width) || Math.Abs(headerFrame.Width - width) > 0.5)) headerFrame.Width = width;
        };
        var message = new TextBlock { Text = note ?? "", Foreground = UiColors.Muted, TextWrapping = TextWrapping.Wrap };

        // As the original: «Закрыть» on the left, «Восстановить» right after it.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var close = SettingsWindow.Command("Закрыть", () => dialog.Close(null)); close.IsCancel = true;
        var restore = SettingsWindow.Command("Восстановить", () => { }); restore.IsDefault = true; restore.IsEnabled = allowed && versions.Count > 0;
        buttons.Children.Add(close); buttons.Children.Add(restore);
        var asking = false;
        async Task Restore()
        {
            if (!restore.IsEnabled || asking) return;
            asking = true;
            try
            {
                if (list.SelectedIndex < 0 || list.SelectedIndex >= versions.Count) { await Inform(dialog, typography, "Не выбрана версия!"); return; }
                var chosen = versions[list.SelectedIndex];
                // A saved copy started by itself restores the program folder (often to its own version): nothing is «in use» there.
                if (!fromSavedCopy && chosen.Version == current && (chosen.Light is null || chosen.Light == currentLight))
                { await Inform(dialog, typography, "Выбранная версия приложения уже используется! Выберите другую версию."); return; }
                if (await Ask(dialog, typography, $"Будет восстановлена версия {chosen.Version}. Продолжать?")) dialog.Close(chosen);
            }
            finally { asking = false; if (dialog.IsVisible) list.Focus(); }
        }
        restore.Click += async (_, _) => await Restore();
        list.DoubleTapped += async (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true) is null) return;
            e.Handled = true; await Restore();
        };
        // The original's action has Ctrl+Enter; Enter in the list restores too (the list would otherwise keep it from the default
        // button), and on «Закрыть» it closes.
        dialog.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var inList = (e.Source as Visual)?.FindAncestorOfType<ListBox>(true) == list;
            if (!inList && !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            e.Handled = true; await Restore();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        dialog.Content = DialogLayout.Frame(body, buttons, message, scrollBody: false);
        typography.ApplyDialog(dialog);
        close.Margin = restore.Margin = new Thickness(0);
        foreach (var cell in cells) cell.TextWrapping = TextWrapping.NoWrap;
        dialog.MinWidth *= UiTypography.DialogScale(typography.DialogSize); dialog.MinHeight *= UiTypography.DialogScale(typography.DialogSize);
        // The menu that opened the window is still closing: the list takes the focus after that, its first row chosen.
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (versions.Count == 0) { close.Focus(); return; }
            list.Focus();
            if (list.ContainerFromIndex(Math.Max(0, list.SelectedIndex)) is ListBoxItem item) item.Focus();
        }, DispatcherPriority.Input);
        return await dialog.ShowDialog<SavedVersion?>(owner);
    }

    private static Task Inform(Window owner, UiTypography typography, string text) => Message(owner, typography, text, question: false);
    private static Task<bool> Ask(Window owner, UiTypography typography, string text) => Message(owner, typography, text, question: true);

    /// <summary>The original's message boxes: «OK», or «Да» (the default) and «Нет» (Escape).</summary>
    private static async Task<bool> Message(Window owner, UiTypography typography, string text, bool question)
    {
        var box = new Window { Title = Title, Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        Button first;
        if (question)
        {
            var yes = SettingsWindow.Command("Да", () => box.Close(true)); yes.IsDefault = true;
            var no = SettingsWindow.Command("Нет", () => box.Close(false)); no.IsCancel = true;
            buttons.Children.Add(yes); buttons.Children.Add(no); first = yes;
        }
        else
        {
            var ok = SettingsWindow.Command("OK", () => box.Close(false)); ok.IsDefault = ok.IsCancel = true;
            buttons.Children.Add(ok); first = ok;
        }
        box.Content = DialogLayout.Frame(DialogLayout.Label(text), buttons);
        typography.ApplyDialog(box);
        box.CanResize = false;
        box.Opened += (_, _) => Dispatcher.UIThread.Post(() => first.Focus(), DispatcherPriority.Input);
        return await box.ShowDialog<bool>(owner);
    }

    /// <summary>After the confirmation: the same as the former «Вернуть предыдущую версию» — the helper replaces the files once every
    /// copy of the program has closed, also when a saved copy is running (Codex audit 15.10: it no longer replaces them itself, past
    /// the exclusive .run.lock). Runs off the UI thread.</summary>
    public static string Restore(Updater updater, SavedVersion version, Version current)
    {
        // By folder: two copies of one version (a busy one and the one kept alongside) are different builds.
        var restored = updater.StageRollback(current, version.Version, version.Folder);
        if (updater.TryLaunchHelper(current, Program.RestartArguments) is { } error)
        {
            return $"Возврат версии {restored} подготовлен, но установщик не запустился ({error.TrimEnd('.')}). " +
                "Версия будет возвращена после следующего запуска и закрытия программы.";
        }
        return $"Версия {restored} будет возвращена после закрытия программы.";
    }
}
