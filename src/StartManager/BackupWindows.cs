using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StartManager.Core;

namespace StartManager;

/// <summary>The message boxes and the parts the backup windows share: the original's white heading panel, a field with «…».</summary>
internal static class BackupDialogs
{
    /// <summary>A message with «OK».</summary>
    public static Task Inform(Window owner, UiTypography typography, string title, string text) => Message(owner, typography, title, text, false, false);

    /// <summary>A question with «Да» and «Нет»; <paramref name="safeDefault"/> makes «Нет» the default answer.</summary>
    public static Task<bool> Ask(Window owner, UiTypography typography, string title, string text, bool safeDefault = true) =>
        Message(owner, typography, title, text, true, safeDefault);

    private static async Task<bool> Message(Window owner, UiTypography typography, string title, string text, bool question, bool safeDefault)
    {
        var box = new Window { Title = title, Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        Button first;
        if (question)
        {
            var yes = SettingsWindow.Command("Да", () => box.Close(true));
            var no = SettingsWindow.Command("Нет", () => box.Close(false)); no.IsCancel = true;
            (safeDefault ? no : yes).IsDefault = true;
            buttons.Children.Add(yes); buttons.Children.Add(no); first = safeDefault ? no : yes;
        }
        else
        {
            var ok = SettingsWindow.Command("OK", () => box.Close(false)); ok.IsDefault = ok.IsCancel = true;
            buttons.Children.Add(ok); first = ok;
        }
        // Long paths and logs are selectable and scroll inside the box.
        var body = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(body, text.Length > 200 ? text[..200] : text);
        box.Content = DialogLayout.Frame(body, buttons);
        typography.ApplyDialog(box);
        box.CanResize = false;
        box.Opened += (_, _) => Dispatcher.UIThread.Post(() => first.Focus(), DispatcherPriority.Input);
        return await box.ShowDialog<bool>(owner);
    }

    /// <summary>The original's white panel above the fields: a title and, when given, a second line (the folder of the base).</summary>
    public static Border Heading(string title, string detail, bool centered = false)
    {
        var panel = new StackPanel { Spacing = 3 };
        var caption = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Text,
            TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left, Classes = { "heading" } };
        panel.Children.Add(caption);
        if (detail.Length > 0)
        {
            var line = new TextBlock { Text = detail, Foreground = UiColors.Muted, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.PathSegmentEllipsis,
                Classes = { "single" } };
            ToolTip.SetTip(line, detail); panel.Children.Add(line);
        }
        return new Border { Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8), Child = panel };
    }

    /// <summary>The heading above the frame of the fields and buttons.</summary>
    public static DockPanel WithHeading(Control heading, Control frame)
    {
        var root = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading); root.Children.Add(frame);
        return root;
    }

    /// <summary>ApplyDialog wraps every text; the lines marked «single» stay on one line and shorten.</summary>
    public static void KeepSingleLines(Window window)
    {
        foreach (var text in window.GetLogicalDescendants().OfType<TextBlock>().Where(x => x.Classes.Contains("single"))) text.TextWrapping = TextWrapping.NoWrap;
    }

    public static TextBox Field(string automation, string value = "")
    {
        var box = new TextBox { Text = value };
        AutomationProperties.SetName(box, automation);
        return box;
    }

    /// <summary>A small button inside a field (the original's TKAVButtonedEdit).</summary>
    public static Button Inner(Control content, string name, Action action)
    {
        var button = new Button { Content = content, Padding = new Thickness(8, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(button, name); AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>The user field with the original's button that reads the last user of the base from def.usr.</summary>
    /// <param name="closed">Whether the window has closed: a late answer is dropped then (Grok audit, part 4).</param>
    public static TextBox UserField(UiTypography typography, BaseEntry entry, string value, Action<string, bool> say, Func<bool> closed)
    {
        var user = Field("Пользователь", value);
        if (BackupForms.OsAuthentication(entry)) user.PlaceholderText = "Аутентификация ОС";
        user.InnerRightContent = Inner(UiIcons.Create("history", typography.IconSize), "Подставить последнего пользователя базы (def.usr)", async () =>
        {
            try
            {
                var last = await Task.Run(() => LastUser.Read(entry.Id));
                if (closed()) return;
                if (last is null) { say("Имя последнего пользователя базы не найдено (файл def.usr 1С).", false); return; }
                user.Text = last; say("", false);
            }
            catch (Exception ex) { if (!closed()) say(LegacyImport.ErrorMessage(ex), true); }
        });
        return user;
    }

    public static TextBox PasswordField(string value)
    {
        var password = new TextBox { Text = value, PasswordChar = '*', RevealPassword = false };
        AutomationProperties.SetName(password, "Пароль");
        return password;
    }

    /// <summary>«Пользователь:» and «Пароль:» side by side as in the original; one under the other when narrow or with a large font.</summary>
    public static Control Pair(string firstCaption, Control first, string secondCaption, Control second)
    {
        var a = DialogLayout.Label(firstCaption); var b = DialogLayout.Label(secondCaption);
        foreach (var x in new Control[] { a, b, first, second }) x.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = new("150,*,Auto,*"), RowDefinitions = new("Auto,Auto,Auto,Auto"), ColumnSpacing = 10 };
        row.Children.Add(a); row.Children.Add(first); row.Children.Add(b); row.Children.Add(second);
        row.SizeChanged += (_, _) =>
        {
            var stacked = row.Bounds.Width < 540 || row.GetValue(TextBlock.FontSizeProperty) > 18;
            var wide = row.Bounds.Width >= 440 && row.GetValue(TextBlock.FontSizeProperty) <= 18;
            if (!stacked)
            {
                row.ColumnDefinitions = new("150,*,Auto,*"); row.RowSpacing = 0; row.ColumnSpacing = 10;
                Place(a, 0, 0); Place(first, 0, 1); Place(b, 0, 2); Place(second, 0, 3);
            }
            else if (wide)
            {
                // Two rows of caption and field, as DialogLayout.Field.
                row.ColumnDefinitions = new("150,*"); row.RowSpacing = 6; row.ColumnSpacing = 10;
                Place(a, 0, 0); Place(first, 0, 1); Place(b, 1, 0); Place(second, 1, 1);
            }
            else
            {
                row.ColumnDefinitions = new("*"); row.RowSpacing = 5; row.ColumnSpacing = 0;
                Place(a, 0, 0); Place(first, 1, 0); Place(b, 2, 0); Place(second, 3, 0);
            }
        };
        return row;
        static void Place(Control control, int r, int c) { Grid.SetRow(control, r); Grid.SetColumn(control, c); }
    }

    /// <summary>Buttons on the left and on the right of the bottom row, as the original's panels; when they do not fit side by side
    /// (a narrow window, a large font) the right ones go under the left ones, still on the right.</summary>
    public static Panel SplitButtons(Control left, Control right)
    {
        var row = new SplitPanel();
        row.Children.Add(left); row.Children.Add(right);
        return row;
    }

    // The choice is made in the layout itself: a choice made after it (SizeChanged) left the second row clipped at large fonts.
    private sealed class SplitPanel : Panel
    {
        private const double Gap = 8;

        private bool Stacked(double width) => Children[0].DesiredSize.Width + Children[1].DesiredSize.Width + Gap > width;

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (var child in Children) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            Size l = Children[0].DesiredSize, r = Children[1].DesiredSize;
            return Stacked(availableSize.Width) ? new Size(Math.Max(l.Width, r.Width), l.Height + Gap + r.Height)
                : new Size(l.Width + Gap + r.Width, Math.Max(l.Height, r.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Size l = Children[0].DesiredSize, r = Children[1].DesiredSize;
            if (Stacked(finalSize.Width))
            {
                Children[0].Arrange(new Rect(0, 0, Math.Min(l.Width, finalSize.Width), l.Height));
                Children[1].Arrange(new Rect(Math.Max(0, finalSize.Width - r.Width), l.Height + Gap, Math.Min(r.Width, finalSize.Width), r.Height));
            }
            else
            {
                var height = Math.Max(l.Height, r.Height);
                Children[0].Arrange(new Rect(0, (height - l.Height) / 2, l.Width, l.Height));
                Children[1].Arrange(new Rect(finalSize.Width - r.Width, (height - r.Height) / 2, r.Width, r.Height));
            }
            return finalSize;
        }
    }

    /// <summary>The fields scroll; the line under them (the busy check, the warning of a load) stays in view at any font.</summary>
    public static Grid Pinned(Control fields, Control pinned)
    {
        var grid = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 10 };
        grid.Children.Add(new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(pinned, 1); grid.Children.Add(pinned);
        return grid;
    }

    /// <summary>The message line under the fields: red for errors, grey for information.</summary>
    public static void Say(TextBlock message, string text, bool error)
    {
        message.Foreground = error ? UiColors.Error : UiColors.Muted; message.Text = text;
    }
}

/// <summary>The window of progress of a backup or a load: the stage, a bar (bytes for archives and copies; running for 1C) and
/// «Отмена». The window cannot be closed while the work runs: closing it cancels the work, which then ends and closes it.</summary>
internal static class BackupProgressWindow
{
    public static async Task<T> Run<T>(Window owner, UiTypography typography, string title, Func<IProgress<BackupProgress>, CancellationToken, Task<T>> work)
    {
        var dialog = new Window { Title = title, Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false };
        var stage = new TextBlock { Text = "Подготовка…", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(stage, "Ход выполнения");
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = true, Height = 10, MinHeight = 10 };
        AutomationProperties.SetName(bar, "Индикатор выполнения");
        var amount = new TextBlock { Foreground = UiColors.Muted, TextWrapping = TextWrapping.Wrap };
        using var source = new CancellationTokenSource();
        var done = false;
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        Button cancel = null!;
        void Cancel()
        {
            if (done || source.IsCancellationRequested) return;
            source.Cancel(); stage.Text = "Отмена… Дождитесь остановки операции."; cancel.IsEnabled = false;
        }
        cancel = SettingsWindow.Command("Отмена", Cancel); cancel.IsCancel = true;
        buttons.Children.Add(cancel);
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(stage); body.Children.Add(bar); body.Children.Add(amount);
        dialog.Content = DialogLayout.Frame(body, buttons);
        typography.ApplyDialog(dialog);
        dialog.CanResize = false;
        // Created on the UI thread: reports from the worker come back to it.
        var progress = new Progress<BackupProgress>(p =>
        {
            if (done || source.IsCancellationRequested) return;
            stage.Text = p.Stage;
            if (p.Total > 0)
            {
                bar.IsIndeterminate = false; bar.Maximum = p.Total; bar.Value = Math.Min(p.Done, p.Total);
                amount.Text = $"{SizeFormat.Short(p.Done)} из {SizeFormat.Short(p.Total)}";
            }
            else { bar.IsIndeterminate = true; amount.Text = ""; }
        });
        dialog.Closing += (_, e) => { if (!done) { e.Cancel = true; Cancel(); } };
        Task<T>? task = null;
        async Task<T> Work()
        {
            try { return await work(progress, source.Token); }
            finally { done = true; dialog.Close(); }
        }
        dialog.Opened += (_, _) => { task = Work(); cancel.Focus(); };
        await dialog.ShowDialog(owner);
        return await (task ?? throw new InvalidOperationException("Окно хода выполнения не открылось."));
    }
}

/// <summary>
/// The original's «Выполнение сценария» (FormScriptExecute.dfm, _Unit206.pas:5765-7330): the white panel with the base and its folder;
/// «Сценарий резервирования» with «Сценарий:» (first «&lt;Использовать сценарий по умолчанию&gt;») and «Описание:»; «Выгрузить в каталог:»;
/// « Имя файла:» with its field (made by the template, typed when checked); «Пользователь:» (its button reads def.usr) and «Пароль:»; the
/// line of the busy check (every 5 s for a file base); «Установить блокировку ИБ» on the left, «Закрыть» and «Выполнить» on the right.
/// </summary>
internal sealed class BackupExecuteWindow : Window
{
    public const string Caption = "Выполнение сценария";
    private readonly BaseEntry entry;
    private readonly BackupScenarioSet set;
    private readonly string generalFolder;
    private readonly IReadOnlyList<(Guid? Id, string Text)> choices;
    private readonly Func<Window, BackupRequest, Task<bool>> execute;
    private readonly ComboBox scenarios;
    private readonly TextBlock note = new() { TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Muted };
    private readonly TextBox folder, fileName, user, password;
    private readonly CheckBox ownName;
    private readonly TextBlock state = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button lockButton, run;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly UiTypography typography;
    private bool updating, folderEdited, running, shut, checking, locking, releasing, closeAsked, allowed = true;
    private int checkSerial;
    // The lock this window set (stage 12): removed after the run or when the window closes, only while it is still that lock.
    private BackupLockGrant? ownLock;
    // The last busy check shown, and the text «Установить блокировку ИБ» last put under the fields (cleared by its next attempt).
    private BaseBusy? lastBusy;
    private string? lockSaid;

    /// <param name="execute">Runs the request (questions, progress and the result are its own); true when the backup was made.</param>
    public BackupExecuteWindow(BaseEntry entry, BackupScenarioSet set, string generalFolder, (string User, string Password) login,
        Func<Window, BackupRequest, Task<bool>> execute, UiTypography typography)
    {
        this.entry = entry; this.set = set; this.generalFolder = generalFolder; this.execute = execute; this.typography = typography;
        Title = Caption; Width = 620; Height = 470; MinWidth = 380; MinHeight = 340; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

        choices = BackupScenarioEdit.Choices(set.Scenarios, null);
        scenarios = new ComboBox { ItemsSource = choices.Select(x => x.Text).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(scenarios, "Сценарий");
        ToolTip.SetTip(scenarios, "Список возможных сценариев резервного копирования");
        var initial = BackupForms.InitialChoice(set, entry);
        scenarios.SelectedIndex = Math.Max(0, choices.ToList().FindIndex(x => x.Id == initial));
        AutomationProperties.SetName(note, "Описание сценария");
        var group = new StackPanel { Spacing = 8 };
        group.Children.Add(DialogLayout.Field("Сценарий:", scenarios));
        group.Children.Add(DialogLayout.Field("Описание:", note));

        folder = BackupDialogs.Field("Выгрузить в каталог");
        ToolTip.SetTip(folder, "Каталог назначения, в который будут выгружены данные");
        var glyph = UiIcons.Create("folder", typography.IconSize); glyph.Margin = new Thickness(6, 0, 0, 0); glyph.VerticalAlignment = VerticalAlignment.Center;
        folder.InnerLeftContent = glyph;
        folder.InnerRightContent = BackupDialogs.Inner(new TextBlock { Text = "…" }, "Выбрать каталог", async () =>
        {
            try
            {
                var chosen = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Выгрузить в каталог", AllowMultiple = false });
                if (!shut && chosen.FirstOrDefault()?.TryGetLocalPath() is { } path) folder.Text = path;
            }
            catch (Exception ex) { if (!shut) BackupDialogs.Say(message, LegacyImport.ErrorMessage(ex), true); }
        });
        folder.TextChanged += (_, _) => { if (!updating) { folderEdited = true; message.Text = ""; } };

        ownName = new CheckBox { Content = DialogLayout.Label(" Имя файла:") };
        AutomationProperties.SetName(ownName, "Имя файла: изменить");
        ToolTip.SetTip(ownName, "Изменить имя файла");
        fileName = BackupDialogs.Field("Имя файла");
        fileName.IsReadOnly = true;
        ToolTip.SetTip(fileName, "Имя файла по шаблону сценария");
        // As the original's chkFileNameClick: checked — the name is typed (the made one is the start); unchecked — made by the template again.
        ownName.IsCheckedChanged += (_, _) =>
        {
            fileName.IsReadOnly = ownName.IsChecked != true;
            if (ownName.IsChecked == true) Dispatcher.UIThread.Post(() => { fileName.Focus(); fileName.SelectAll(); }, DispatcherPriority.Input);
            else MakeName(DateTime.Now);
        };

        user = BackupDialogs.UserField(typography, entry, login.User, (text, error) => BackupDialogs.Say(message, text, error), () => shut);
        password = BackupDialogs.PasswordField(login.Password);

        var stateFrame = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1), Padding = new Thickness(11, 6), MinHeight = 40, Child = state };

        var fields = new StackPanel { Spacing = 10 };
        fields.Children.Add(DialogLayout.Section("Сценарий резервирования", group));
        fields.Children.Add(DialogLayout.Field("Выгрузить в каталог:", folder));
        fields.Children.Add(DialogLayout.Field(ownName, fileName));
        fields.Children.Add(BackupDialogs.Pair("Пользователь:", user, "Пароль:", password));


        // As the original: «Установить блокировку ИБ» on the left; «Закрыть», then «Выполнить» on the right.
        lockButton = SettingsWindow.Command("Установить блокировку ИБ", async () => await SetLock());
        ToolTip.SetTip(lockButton, entry.Kind == ConnectionKind.File ? "Попробовать установить блокировку (файл 1Cv8.cdn на 60 минут)"
            : "Блокировка файлом 1Cv8.cdn доступна только для файлового варианта информационной базы");
        ToolTip.SetShowOnDisabled(lockButton, true);
        lockButton.IsEnabled = entry.Kind == ConnectionKind.File;
        var close = SettingsWindow.Command("Закрыть", () => Close()); close.IsCancel = true;
        ToolTip.SetTip(close, "Отказаться от выполнения сценария и закрыть форму");
        run = SettingsWindow.Command("Выполнить", async () => await Run()); run.IsDefault = true; run.FontWeight = FontWeight.Bold;
        ToolTip.SetTip(run, "Запустить сценарий");
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        right.Children.Add(close); right.Children.Add(run);
        var buttons = BackupDialogs.SplitButtons(lockButton, right);
        foreach (var button in new[] { lockButton, close, run }) button.MinWidth = 90;
        // The original's TabOrder: «Выполнить» (butExecute, TabOrder 0 of Panel1) is its only focusable button — «Закрыть» and the lock are
        // speed buttons. Here it comes first too, then «Закрыть», then the lock (Grok audit, part 4).
        KeyboardNavigation.SetTabIndex(right, 0); KeyboardNavigation.SetTabIndex(lockButton, 1);
        KeyboardNavigation.SetTabNavigation(right, KeyboardNavigationMode.Local);
        KeyboardNavigation.SetTabIndex(run, 0); KeyboardNavigation.SetTabIndex(close, 1);

        var heading = BackupDialogs.Heading(entry.Name, entry.Location);
        Content = BackupDialogs.WithHeading(heading, DialogLayout.Frame(BackupDialogs.Pinned(fields, stateFrame), buttons, message, scrollBody: false));
        scenarios.SelectionChanged += (_, _) => Refresh();
        typography.ApplyDialog(this);
        BackupDialogs.KeepSingleLines(this);
        MinWidth *= UiTypography.DialogScale(typography.DialogSize); MinHeight *= UiTypography.DialogScale(typography.DialogSize);
        Refresh();
        // A close asked while the lock of the run is being removed is done right after it, once its result is recorded (KeptLock is
        // read by the main window when this window is gone; Codex re-check 11).
        Closing += (_, e) => { if (running || locking) { e.Cancel = true; closeAsked |= releasing; } };
        // The run disables the content, «Закрыть» (IsCancel) with it: Escape then asks to close as the title bar's cross does
        // (Codex re-check 12, P3). Tunnel: the key comes whatever is focused.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || !(running || locking)) return;
            e.Handled = true; Close();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            shut = true; timer.Stop();
            // Set and never used by a run: removed too. A lock changed meanwhile by someone else stays (BackupLock.Release).
            // The window is gone: a lock that stays is told by the main window and logged (base, file, what to do; no code).
            if (ownLock is { } grant) { ownLock = null; Kept(BackupLock.Release(grant)); }
        };
        timer.Tick += async (_, _) => await Check();
        Opened += async (_, _) =>
        {
            Dispatcher.UIThread.Post(() => scenarios.Focus(), DispatcherPriority.Input);
            await Check();
            if (entry.Kind == ConnectionKind.File && !shut) timer.Start();
        };
    }

    private BackupScenario? Current() =>
        scenarios.SelectedIndex >= 0 && scenarios.SelectedIndex < choices.Count ? BackupForms.Scenario(set, choices[scenarios.SelectedIndex].Id) : null;

    // The original's cbScriptsChange and PathUpdate: the description, the folder (until the user typed one) and the name.
    private void Refresh()
    {
        var scenario = Current();
        note.Text = scenario?.ShownDescription ?? "";
        if (!folderEdited)
        {
            updating = true;
            try { folder.Text = BackupTarget.DefaultFolder(entry, scenario, generalFolder); if (message.Foreground == UiColors.Error) message.Text = ""; }
            catch (ArgumentException ex) { folder.Text = ""; BackupDialogs.Say(message, ex.Message, true); }
            finally { updating = false; }
        }
        if (ownName.IsChecked != true) MakeName(DateTime.Now);
    }

    private void MakeName(DateTime time)
    {
        if (Current() is { } scenario) fileName.Text = BackupForms.FileName(scenario, BackupForms.Values(entry, time));
    }

    private void UpdateRun() => run.IsEnabled = allowed && !running && !locking;

    // The original's Timer1 and VerifyPossibilityUnloading; the check reads the disk off the UI thread. A late answer after closing, or
    // one that began before a run (the base is then held by our own dump), is dropped; «force» is the check after the run.
    private async Task Check(bool force = false)
    {
        if ((checking && !force) || shut || running) return;
        if (entry.Kind != ConnectionKind.File) { ShowState(BackupForms.DumpState(entry, new BaseBusy(BaseBusyState.NotChecked))); return; }
        checking = true;
        var serial = ++checkSerial;
        bool Fresh() => !shut && !running && serial == checkSerial;
        try
        {
            var busy = await Task.Run(() => BaseBusyCheck.Check(entry));
            if (Fresh()) { lastBusy = busy; ShowState(BackupForms.DumpState(entry, busy, ownLock is not null)); }
        }
        catch (Exception ex) { if (Fresh()) ShowState(new BackupBusyView("Не удалось проверить, открыта ли информационная база: " + ex.Message, true, false)); }
        finally { if (serial == checkSerial) checking = false; }
    }

    private void ShowState(BackupBusyView view)
    {
        state.Text = view.Text; state.Foreground = view.Alarm ? UiColors.Error : UiColors.BaseText;
        allowed = view.Allowed; UpdateRun();
    }

    // One write at a time: the button is off and the window stays open while the file is written (Grok audit, part 4).
    private async Task SetLock()
    {
        if (entry.Kind != ConnectionKind.File || locking || running) return;
        var location = entry.Location;
        locking = true; lockButton.IsEnabled = false; UpdateRun();
        var set = false;
        try
        {
            string text;
            BackupLockGrant? grant;
            (set, text, grant) = await Task.Run(() => BackupForms.SetLock(location, DateTime.Now));
            if (grant is not null)
            {
                // Closed while it was written: nothing will run, the lock goes at once.
                if (shut) { Kept(BackupLock.Release(grant)); return; }
                ownLock = grant;
            }
            if (shut) return;
            // The text of an earlier attempt is not left under the new result (deferred note of 20.10); other messages stay.
            if (message.Text == lockSaid) message.Text = "";
            lockSaid = null;
            if (set) { BackupDialogs.Say(message, lockSaid = text, false); if (lastBusy is { } busy) ShowState(BackupForms.DumpState(entry, busy, true)); }
            else if (text == BackupForms.LockSet) await BackupDialogs.Inform(this, typography, Caption, text);
            else BackupDialogs.Say(message, lockSaid = text, true);
        }
        finally
        {
            locking = false;
            if (!shut) { lockButton.IsEnabled = !set; UpdateRun(); }
        }
    }

    /// <summary>A lock not removed after the window closed (Codex re-check, 19.10): the text for the main window's status line.</summary>
    public string? KeptLock { get; private set; }

    private void Kept(BackupLockRelease released)
    {
        // The log holds no paths or names (DiagnosticLog): the base's ID, the reason and the private file name; the full text with
        // the folder and what to do is in the main window's status line.
        if (BackupLock.Record(entry, released, AppLog.LockKept) is { } text) KeptLock = text;
    }

    private async Task ReleaseLock()
    {
        if (ownLock is not { } grant) return;
        ownLock = null;
        BackupLockRelease released;
        releasing = true;
        try { released = await Task.Run(() => BackupLock.Release(grant)); }
        finally { releasing = false; }
        // Recorded at once: a successful run closes the window right after this (Codex re-check 10).
        Kept(released);
        if (shut) return;
        // The button follows the lock: 1Cv8.cdn removed (also when only its second name stays) — a new lock may be set.
        if (released.Removed || released.LockGone) lockButton.IsEnabled = entry.Kind == ConnectionKind.File;
        if (!released.Removed) BackupDialogs.Say(message, released.Message ?? "", true);
    }

    private async Task Run()
    {
        if (running || locking || !run.IsEnabled) return;
        if (Current() is not { } scenario) { BackupDialogs.Say(message, "Не найдены параметры сценария!", true); return; }
        var target = folder.Text?.Trim() ?? "";
        if (BackupScenarioEdit.InvalidFolder(target, OperatingSystem.IsWindows())) { BackupDialogs.Say(message, "Путь содержит недопустимые символы!", true); folder.Focus(); return; }
        // A checked, cleared « Имя файла:» is refused rather than silently made by the template (Grok audit, part 4).
        if (BackupForms.RequestName(ownName.IsChecked == true, fileName.Text) is not { } name)
        { BackupDialogs.Say(message, BackupForms.NoFileName, true); fileName.Focus(); return; }
        // The name of the moment of the start: the template's time macros are those of the backup.
        var time = DateTime.Now;
        if (ownName.IsChecked != true) MakeName(time);
        var request = new BackupRequest
        {
            Base = entry, Scenario = scenario, Folder = target, FileName = name,
            Credentials = BackupForms.Credentials(entry, user.Text ?? "", password.Text ?? ""), Values = BackupForms.Values(entry, time)
        };
        // As the original disables Timer1 for the run: the check would see the base held by our own dump and show it «busy».
        running = true; timer.Stop(); checkSerial++; UpdateRun(); message.Text = "";
        var content = Content as Control;
        if (content is not null) content.IsEnabled = false;
        var made = false;
        try { made = await execute(this, request); }
        finally
        {
            // The lock of the run goes whatever the result (success, failure, cancel or an exception of the run). The window stays
            // «running» (it cannot be closed) until the release is recorded (Codex re-check 11).
            try { await ReleaseLock(); }
            finally
            {
                running = false;
                if (content is not null) content.IsEnabled = true;
                UpdateRun();
            }
        }
        if (made || closeAsked) { Close(); return; }
        if (shut) return;
        run.Focus();
        // The state once after the run, then the timer again.
        await Check(force: true);
        if (entry.Kind == ConnectionKind.File && !shut && !running) timer.Start();
    }
}

/// <param name="Record">The chosen backup.</param>
/// <param name="File">The file to load: the main one, or a copy of it in a folder 2–3.</param>
internal sealed record BackupListChoice(BackupRecord Record, string File);

/// <summary>
/// The original's «Список выгрузок» (FormBackupList.dfm, _Unit206.pas:2052-3270): the white panel with the base; «Удалить» on the left of the
/// toolbar, «Свернуть» and «Развернуть» on the right; the tree of the journal — the groups «Выгрузка данных…», «Выгрузка конфигурации…»,
/// «ZIP-архивы…» with « [n]», the backups by their time — in the columns «Наименование» and «Размер»; the file of the chosen backup under it
/// (a click opens its folder); «Закрыть» on the left, «Загрузить» on the right. Del removes the file and the record after a question.
/// </summary>
internal static class BackupListWindow
{
    public const string Caption = "Список выгрузок";

    public static async Task<BackupListChoice?> Show(Window owner, UiTypography typography, BaseEntry entry, BackupJournalStore journal, BackupJournal first)
    {
        var dialog = new Window { Title = Caption, Width = 580, Height = 420, MinWidth = 380, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        IReadOnlyList<BackupListGroup> groups = BackupList.Groups(first, entry.Id);
        var expanded = new HashSet<BackupFormat>(Enum.GetValues<BackupFormat>());
        var rows = new List<(BackupListGroup Group, BackupRecord? Record)>();
        var busy = false;
        double TextWidth(string text) => Math.Ceiling(UiTypography.Measure(text, typography.DialogFamily, typography.DialogSize).Width);
        var sizeWidth = Math.Ceiling(Math.Max(TextWidth("Размер") * 1.1, TextWidth("999,9 МБ")) + 16);
        var columns = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"*,{sizeWidth}");
        var indent = Math.Round(typography.DialogSize * 1.3);
        var cells = new List<TextBlock>();
        Grid Row(Control first, string size, bool header)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
            var name = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = first };
            row.Children.Add(name);
            var cell = new TextBlock { Text = size, Margin = new Thickness(5, 3), TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Right,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal, Foreground = header ? UiColors.Muted : UiColors.Text, VerticalAlignment = VerticalAlignment.Center };
            cells.Add(cell);
            Grid.SetColumn(cell, 1); row.Children.Add(cell);
            return row;
        }
        var headerText = new TextBlock { Text = "Наименование", Margin = new Thickness(5, 3), FontWeight = FontWeight.SemiBold, Foreground = UiColors.Muted, TextWrapping = TextWrapping.NoWrap };
        cells.Add(headerText);
        var header = Row(headerText, "Размер", true);
        var headerFrame = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(1, 1, 1, 0), Background = UiColors.Surface, Child = header,
            HorizontalAlignment = HorizontalAlignment.Left };
        var list = new ListBox { Background = UiColors.Field, BorderBrush = UiColors.Line, BorderThickness = new Thickness(1) };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
            new Setter(ListBoxItem.MinHeightProperty, 0d) } });
        AutomationProperties.SetName(list, "Выгрузки информационной базы");
        // The item itself carries the whole text (a group's title with its count, a record's time with its size): the cells of a row are
        // separate texts, and the title may be cut.
        var names = new List<string>();
        list.ContainerPrepared += (_, e) => { if (e.Index >= 0 && e.Index < names.Count) AutomationProperties.SetName(e.Container, names[e.Index]); };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        list.LayoutUpdated += (_, _) =>
        {
            if (list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll) return;
            var width = scroll.Viewport.Width + 2;
            if (width > 2 && (double.IsNaN(headerFrame.Width) || Math.Abs(headerFrame.Width - width) > 0.5)) headerFrame.Width = width;
        };

        // The original's lbStatus: the file of the chosen backup as a link that opens its folder; the title of a chosen group.
        var link = new Button { Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new TextBlock { TextTrimming = TextTrimming.PathSegmentEllipsis, TextWrapping = TextWrapping.NoWrap, Classes = { "single" } } };
        AutomationProperties.SetName(link, "Файл выгрузки");
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };

        var delete = Tool("delete", "Удалить", "Удалить · Del");
        var collapse = Tool("collapse-all", "Свернуть", "Свернуть");
        var expand = Tool("expand-all", "Развернуть", "Развернуть");
        Button Tool(string icon, string name, string tip)
        {
            var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            face.Children.Add(UiIcons.Create(icon, typography.IconSize)); face.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var button = SettingsWindow.Command(name, () => { }); button.Content = face; button.Margin = new Thickness(0);
            ToolTip.SetTip(button, tip); return button;
        }
        var tools = new DockPanel();
        DockPanel.SetDock(delete, Dock.Left); tools.Children.Add(delete);
        var tree = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        tree.Children.Add(collapse); tree.Children.Add(expand); tools.Children.Add(tree);

        var close = SettingsWindow.Command("Закрыть", () => dialog.Close(null)); close.IsCancel = true;
        var load = SettingsWindow.Command("Загрузить", () => { }); load.IsDefault = true; load.FontWeight = FontWeight.Bold;
        var buttons = new DockPanel();
        DockPanel.SetDock(close, Dock.Left); buttons.Children.Add(close);
        load.HorizontalAlignment = HorizontalAlignment.Right; buttons.Children.Add(load);

        (BackupListGroup Group, BackupRecord? Record)? Current() => list.SelectedIndex >= 0 && list.SelectedIndex < rows.Count ? rows[list.SelectedIndex] : null;
        void ShowRows(BackupFormat? keepGroup = null, Guid? keepRecord = null)
        {
            rows.Clear();
            foreach (var group in groups)
            {
                rows.Add((group, null));
                if (expanded.Contains(group.Format)) foreach (var record in group.Records) rows.Add((group, record));
            }
            names.Clear();
            names.AddRange(rows.Select(x => x.Record is null ? x.Group.Text : $"{BackupList.Text(x.Record)} · {SizeFormat.Short(x.Record.Size)}"));
            list.ItemsSource = rows.Select(x =>
            {
                // The arrow, the title (cut with an ellipsis when the window is narrow or the font large) and the count of a group, which
                // stays whole (Codex audit, part 4).
                var cell = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(5 + (x.Record is null ? 0 : indent), 3, 5, 3) };
                if (x.Record is null)
                {
                    var arrow = UiIcons.Create(expanded.Contains(x.Group.Format) ? "chevron-down" : "chevron-right", typography.IconSize);
                    arrow.Margin = new Thickness(0, 0, 4, 0); arrow.VerticalAlignment = VerticalAlignment.Center;
                    var format = x.Group.Format;
                    arrow.PointerPressed += (_, e) => { e.Handled = true; Toggle(format); };
                    cell.Children.Add(arrow);
                }
                var text = new TextBlock { Text = x.Record is null ? x.Group.Title : BackupList.Text(x.Record), TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = x.Record is null ? FontWeight.SemiBold : FontWeight.Normal };
                cells.Add(text);
                Grid.SetColumn(text, 1); cell.Children.Add(text);
                if (x.Record is null)
                {
                    var count = new TextBlock { Text = x.Group.Count, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
                        FontWeight = FontWeight.SemiBold };
                    cells.Add(count);
                    Grid.SetColumn(count, 2); cell.Children.Add(count);
                }
                var row = Row(cell, x.Record is null ? "" : SizeFormat.Short(x.Record.Size), false);
                ToolTip.SetTip(row, x.Record is null ? x.Group.Text : BackupList.MainFile(x.Record));
                return new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = row } as Control;
            }).ToList();
            var index = rows.FindIndex(x => keepRecord is { } id ? x.Record?.Id == id : keepGroup is { } g && x.Record is null && x.Group.Format == g);
            list.SelectedIndex = index >= 0 ? index : rows.FindIndex(x => x.Record is not null) is var firstRecord and >= 0 ? firstRecord : rows.Count > 0 ? 0 : -1;
            Update();
        }
        void Toggle(BackupFormat format, bool? open = null)
        {
            if (open ?? !expanded.Contains(format)) expanded.Add(format); else expanded.Remove(format);
            ShowRows(keepGroup: format);
        }
        void Update()
        {
            var current = Current();
            var file = current?.Record is { } record ? BackupList.MainFile(record) : "";
            var face = (TextBlock)link.Content!;
            face.Text = current is null ? (groups.Count == 0 ? BackupList.Empty : "") : current.Value.Record is null ? current.Value.Group.Title : file;
            face.TextDecorations = file.Length > 0 ? TextDecorations.Underline : null;
            link.Foreground = file.Length > 0 ? UiColors.SectionText : UiColors.Muted;
            link.Cursor = file.Length > 0 ? new Cursor(StandardCursorType.Hand) : null;
            link.Focusable = file.Length > 0;
            ToolTip.SetTip(link, file.Length > 0 ? "Открыть каталог: " + file : null);
            load.IsEnabled = delete.IsEnabled = current?.Record is not null && !busy;
            collapse.IsEnabled = expand.IsEnabled = groups.Count > 0;
        }
        list.SelectionChanged += (_, _) => { Update(); };

        async Task Delete()
        {
            if (busy || Current() is not { Record: { } record } current) return;
            var file = BackupList.MainFile(record);
            if (!await BackupDialogs.Ask(dialog, typography, Caption, BackupList.DeleteQuestion + "\n" + file)) { list.Focus(); return; }
            busy = true; Update();
            try
            {
                var warnings = await Task.Run(() => journal.Delete(record.Id));
                var next = await Task.Run(journal.Load);
                groups = BackupList.Groups(next, entry.Id);
                ShowRows(keepGroup: current.Group.Format);
                BackupDialogs.Say(message, warnings.Count == 0 ? "Файл архива удалён: " + file : string.Join(" ", warnings), warnings.Count > 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { BackupDialogs.Say(message, "Файл архива не удалён: " + LegacyImport.ErrorMessage(ex), true); }
            finally { busy = false; Update(); list.Focus(); }
        }
        async Task Load()
        {
            if (busy || Current() is not { Record: { } record }) return;
            busy = true; Update();
            try
            {
                // A file on a network share may answer slowly: the check is off the UI thread.
                var source = await Task.Run(() => BackupList.Source(record));
                if (source is null) { await BackupDialogs.Inform(dialog, typography, Caption, BackupList.NotFound + "\n" + BackupList.MainFile(record)); return; }
                dialog.Close(new BackupListChoice(record, source));
            }
            // The choice of a copy is not to crash the window on a folder it could not read (Codex second recheck, part 4).
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                AppLog.Warning("backup.list.source_failed", ex);
                BackupDialogs.Say(message, "Файл архива не проверен: " + LegacyImport.ErrorMessage(ex), true);
            }
            finally { busy = false; if (dialog.IsVisible) { Update(); list.Focus(); } }
        }
        delete.Click += async (_, _) => await Delete();
        load.Click += async (_, _) => await Load();
        collapse.Click += (_, _) => { expanded.Clear(); ShowRows(keepGroup: Current()?.Group.Format); };
        expand.Click += (_, _) => { foreach (var format in Enum.GetValues<BackupFormat>()) expanded.Add(format); ShowRows(Current()?.Group.Format, Current()?.Record?.Id); };
        link.Click += async (_, _) =>
        {
            if (Current()?.Record is not { } record || Path.GetDirectoryName(BackupList.MainFile(record)) is not { Length: > 0 } folder) return;
            try
            {
                var exists = await Task.Run(() => Directory.Exists(folder));
                if (!exists || !await dialog.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)))
                    BackupDialogs.Say(message, $"Не удалось открыть каталог {folder}.", true);
            }
            catch (Exception ex) { BackupDialogs.Say(message, $"Не удалось открыть каталог {folder}: {ex.Message}", true); }
        };
        // As a tree: Right opens a group, Left closes it or goes to the group; Enter loads a backup and opens or closes a group; Del removes.
        list.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (Current() is not { } current) return;
            var index = list.SelectedIndex;
            switch (e.Key)
            {
                case Key.Right when current.Record is null:
                    if (expanded.Contains(current.Group.Format)) { if (index + 1 < rows.Count && rows[index + 1].Record is not null) list.SelectedIndex = index + 1; }
                    else Toggle(current.Group.Format, true);
                    e.Handled = true; break;
                case Key.Left:
                    if (current.Record is null) { if (expanded.Contains(current.Group.Format)) Toggle(current.Group.Format, false); }
                    else list.SelectedIndex = rows.FindIndex(x => x.Record is null && x.Group.Format == current.Group.Format);
                    e.Handled = true; break;
                case Key.Enter when current.Record is null && e.KeyModifiers == KeyModifiers.None:
                    Toggle(current.Group.Format); e.Handled = true; break;
                case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                    e.Handled = true; await Load(); return;
                case Key.Delete when e.KeyModifiers == KeyModifiers.None:
                    e.Handled = true; await Delete(); return;
            }
            if (!e.Handled || list.SelectedIndex < 0) return;
            list.ScrollIntoView(list.SelectedIndex);
            (list.ContainerFromIndex(list.SelectedIndex) as Control)?.Focus();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        list.DoubleTapped += async (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true) is null || Current() is not { } current) return;
            e.Handled = true;
            if (current.Record is null) Toggle(current.Group.Format); else await Load();
        };

        var body = new Grid { RowDefinitions = new("Auto,Auto,*,Auto,Auto"), RowSpacing = 6 };
        body.Children.Add(tools);
        Grid.SetRow(headerFrame, 1); body.Children.Add(headerFrame);
        Grid.SetRow(list, 2); body.Children.Add(list);
        Grid.SetRow(link, 3); body.Children.Add(link);
        Grid.SetRow(message, 4); body.Children.Add(message);
        dialog.Content = BackupDialogs.WithHeading(BackupDialogs.Heading(entry.Name, entry.Location), DialogLayout.Frame(body, buttons, scrollBody: false));
        typography.ApplyDialog(dialog);
        BackupDialogs.KeepSingleLines(dialog);
        close.Margin = load.Margin = new Thickness(0);
        dialog.MinWidth *= UiTypography.DialogScale(typography.DialogSize); dialog.MinHeight *= UiTypography.DialogScale(typography.DialogSize);
        ShowRows();
        foreach (var cell in cells) cell.TextWrapping = TextWrapping.NoWrap;
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            list.Focus();
            if (list.ContainerFromIndex(Math.Max(0, list.SelectedIndex)) is ListBoxItem item) item.Focus();
        }, DispatcherPriority.Input);
        return await dialog.ShowDialog<BackupListChoice?>(owner);
    }
}

/// <param name="File">«Файл (dt):» / «Файл (cf):».</param>
internal sealed record BackupLoadChoice(string File, string User, string Password);

/// <summary>
/// The original's «Загрузка данных» (FormLoad.dfm, _Unit206.pas:16362-16700) for «Загрузить информационную базу из файла (*.dt)» and
/// «Загрузить конфигурацию из файла (*.cf)»: the white panel with «Загрузка данных конфигурации (DT)» / «Загрузка структуры конфигурации
/// (CF)», «Файл (dt):» with its file choice, «Учетные данные для подключения» (the user's button reads def.usr), the red warning that
/// turns into «Информационная база занята!…» (checked every 3.5 s, the button disabled meanwhile), «Отмена» and «Выполнить загрузку».
/// </summary>
internal static class BackupLoadWindow
{
    public const string Caption = "Загрузка данных";

    public static async Task<BackupLoadChoice?> Show(Window owner, UiTypography typography, BaseEntry entry, BackupFormat format, (string User, string Password) login)
    {
        var dt = format == BackupFormat.Dt;
        var dialog = new Window { Title = Caption, Width = 560, Height = 380, MinWidth = 380, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var shut = false;
        var file = BackupDialogs.Field(dt ? "Файл (dt)" : "Файл (cf)");
        // The original's filters: «Выгрузка ИБ (*.dt)» / «Выгрузка конфигурации (*.cf)» and «Все файлы».
        var filter = dt ? "Выгрузка ИБ (*.dt)" : "Выгрузка конфигурации (*.cf)";
        file.InnerRightContent = BackupDialogs.Inner(new TextBlock { Text = "…" }, "Выбрать файл", async () =>
        {
            try
            {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = filter, AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType(filter) { Patterns = [dt ? "*.dt" : "*.cf"] }, new FilePickerFileType("Все файлы") { Patterns = ["*"] }] });
                if (!shut && files.FirstOrDefault()?.TryGetLocalPath() is { } path) file.Text = path;
            }
            catch (Exception ex) { if (!shut) BackupDialogs.Say(message, LegacyImport.ErrorMessage(ex), true); }
        });
        file.TextChanged += (_, _) => message.Text = "";
        var user = BackupDialogs.UserField(typography, entry, login.User, (text, error) => BackupDialogs.Say(message, text, error), () => shut);
        var password = BackupDialogs.PasswordField(login.Password);
        var credentials = new StackPanel { Spacing = 8 };
        credentials.Children.Add(DialogLayout.Field("Пользователь:", user));
        credentials.Children.Add(DialogLayout.Field("Пароль:", password));

        // The original's Image1 and lbStatusText: the warning, and the busy base. The glyph has the field's colour: white on the light
        // theme's red, dark on the dark theme's pink (white on it was about 1.7:1; Grok audit, part 4).
        var sign = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = UiColors.Error, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "!", Foreground = UiColors.Field, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center } };
        var warning = new TextBlock { Text = BackupForms.LoadWarning, Foreground = UiColors.Error, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var warningRow = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        warningRow.Children.Add(sign); Grid.SetColumn(warning, 1); warningRow.Children.Add(warning);

        var fields = new StackPanel { Spacing = 12 };
        fields.Children.Add(DialogLayout.Field(dt ? "Файл (dt):" : "Файл (cf):", file));
        fields.Children.Add(DialogLayout.Section("Учетные данные для подключения", credentials));


        // As the original: «Отмена» on the left, «Выполнить загрузку» on the right.
        var cancel = SettingsWindow.Command("Отмена", () => dialog.Close(null)); cancel.IsCancel = true;
        var start = SettingsWindow.Command("Выполнить загрузку", () => { }); start.IsDefault = true; start.FontWeight = FontWeight.Bold;
        var buttons = new DockPanel();
        DockPanel.SetDock(cancel, Dock.Left); buttons.Children.Add(cancel);
        start.HorizontalAlignment = HorizontalAlignment.Right; buttons.Children.Add(start);

        var checking = false; var allowed = true; var asking = false;
        void Show(BackupBusyView view) { warning.Text = view.Text; allowed = view.Allowed; start.IsEnabled = allowed && !asking; }
        async Task Check()
        {
            if (checking || shut || entry.Kind != ConnectionKind.File) return;
            checking = true;
            try { var busy = await Task.Run(() => BaseBusyCheck.Check(entry)); if (!shut) Show(BackupForms.LoadState(entry, busy)); }
            catch (Exception ex) { AppLog.Warning("backup.busy_check.failed", ex); }
            finally { checking = false; }
        }
        async Task Start()
        {
            if (asking || !start.IsEnabled) return;
            asking = true; start.IsEnabled = false;
            try
            {
                var path = file.Text?.Trim() ?? "";
                if (path.Length == 0) { await BackupDialogs.Inform(dialog, typography, Caption, BackupRestore.NoFile); file.Focus(); return; }
                var exists = Path.IsPathFullyQualified(path) && await Task.Run(() => File.Exists(path));
                if (shut) return;
                if (!exists) { await BackupDialogs.Inform(dialog, typography, Caption, BackupRestore.MissingFile); file.Focus(); return; }
                // The window loads what it says: a configuration is not loaded by «*.dt», nor an archive by either.
                if (BackupRestore.FormatOf(path) != format)
                { await BackupDialogs.Inform(dialog, typography, Caption, dt ? "Выберите файл выгрузки информационной базы (*.dt)." : "Выберите файл конфигурации (*.cf)."); file.Focus(); return; }
                if (BackupForms.AsksForUser(entry, user.Text ?? "") && !await BackupDialogs.Ask(dialog, typography, Caption, BackupForms.EmptyUserQuestion, safeDefault: false))
                { user.Focus(); return; }
                dialog.Close(new BackupLoadChoice(path, user.Text ?? "", password.Text ?? ""));
            }
            finally { asking = false; if (!shut) start.IsEnabled = allowed; }
        }
        start.Click += async (_, _) => await Start();

        var heading = BackupDialogs.Heading(dt ? "Загрузка данных конфигурации (DT)" : "Загрузка структуры конфигурации (CF)", "", centered: true);
        dialog.Content = BackupDialogs.WithHeading(heading, DialogLayout.Frame(BackupDialogs.Pinned(fields, warningRow), buttons, message, scrollBody: false));
        typography.ApplyDialog(dialog);
        BackupDialogs.KeepSingleLines(dialog);
        cancel.Margin = start.Margin = new Thickness(0);
        dialog.MinWidth *= UiTypography.DialogScale(typography.DialogSize); dialog.MinHeight *= UiTypography.DialogScale(typography.DialogSize);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
        timer.Tick += async (_, _) => await Check();
        dialog.Closed += (_, _) => { shut = true; timer.Stop(); };
        dialog.Opened += async (_, _) =>
        {
            Dispatcher.UIThread.Post(() => file.Focus(), DispatcherPriority.Input);
            await Check();
            if (entry.Kind == ConnectionKind.File && !shut) timer.Start();
        };
        return await dialog.ShowDialog<BackupLoadChoice?>(owner);
    }
}
