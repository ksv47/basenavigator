using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    // A newer release found by the startup check of the «уведомлять» way: offered until it is downloaded or the program closes.
    private ReleaseInfo? offeredRelease;
    private string offeredRepository = "";
    private bool updateDownloading, updateOfferOpen;
    private readonly PendingUpdateNotice pendingUpdate = new();
    private Avalonia.Threading.DispatcherTimer? pendingUpdateTimer;

    /// <summary>Shows the result of the startup check once the list is neither read nor written and, for the offer, no other
    /// window of the program is open; until then it is retried every half second.</summary>
    private void PublishPendingUpdate()
    {
        if (closed) { pendingUpdateTimer?.Stop(); return; }
        var (publish, show) = pendingUpdate.Take(reading || writing || updateOfferOpen, OwnedWindows.Any(x => x != shortHelp));
        if (publish) status.Text = loadFailed ? updateNotice + " · " + status.Text : SourceDescription();
        if (show) ShowUpdateOffer(automatic: true);
        if (!pendingUpdate.Waiting) { pendingUpdateTimer?.Stop(); return; }
        if (pendingUpdateTimer is null)
        {
            pendingUpdateTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            pendingUpdateTimer.Tick += (_, _) => PublishPendingUpdate();
        }
        pendingUpdateTimer.Start();
    }

    /// <summary>The status bar opens the offer while it shows the notice of the found version.</summary>
    private bool UpdateOfferShown => offeredRelease is not null && !updateDownloading && updateNotice is { Length: > 0 } notice &&
        status.Text?.Contains(notice, StringComparison.Ordinal) == true;

    private void AttachUpdateNotice()
    {
        void Show()
        {
            var clickable = UpdateOfferShown;
            status.Cursor = clickable ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            ToolTip.SetTip(status, clickable ? status.Text + "\nЩёлкните, чтобы открыть окно обновления." : status.Text);
        }
        status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) Show(); };
        status.PointerPressed += (_, e) =>
        {
            if (!UpdateOfferShown || !e.GetCurrentPoint(status).Properties.IsLeftButtonPressed) return;
            e.Handled = true; ShowUpdateOffer(automatic: false);
        };
        Show();
    }

    // The original's FormUpdateInfo «Обновление StartManager 1.4»: the icon and the heading, the versions, the release
    // description; «Напомнить позже» on the left, «Загрузить» on the right (bold, the default). By the user's choice there is
    // no countdown, no automatic download and no «Загрузить дистрибутив»: «Загрузить» is «Установить обновление» of
    // «Настройки → Автообновление» — the release is prepared now and applied by the helper after the program closes.
    // While it downloads the window stays with the stage and a bar, its buttons wait; closing it cancels the download (gh is
    // stopped, its folder removed) and the window closes once it has stopped — as the window of a backup.
    private async void ShowUpdateOffer(bool automatic)
    {
        if (offeredRelease is not { } release || closed || updateDownloading || updateOfferOpen) return;
        // Opened from the notice: it does not come up by itself afterwards (PendingUpdateNotice decides the automatic one).
        if (!automatic) pendingUpdate.Settled();
        if (Updater.ChangedInThisProcess)
        {
            offeredRelease = null;
            status.Text = "Обновление уже подготовлено в этом сеансе — перезапустите программу.";
            return;
        }
        var current = Program.CurrentVersion;
        var dialog = new Window { Title = UpdateOffer.Title, Width = 556, Height = 347, MinWidth = 380, MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        heading.Children.Add(ProgramImage(Math.Round(32 * Math.Max(1, typography.DialogSize / 13))));
        var texts = new StackPanel { Spacing = 6 };
        texts.Children.Add(new TextBlock { Text = UpdateOffer.Heading, FontWeight = FontWeight.Bold });
        texts.Children.Add(new TextBlock { Text = UpdateOffer.Message(release.Version, current) });
        texts.Children.Add(new TextBlock { Text = "Информация о выпуске:", Margin = new Thickness(0, 4, 0, 0) });
        Grid.SetColumn(texts, 1); heading.Children.Add(texts);
        var notes = new TextBox { Text = UpdateOffer.Notes(release.Notes), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Background = UiColors.Field, MinHeight = 60 };
        ScrollViewer.SetVerticalScrollBarVisibility(notes, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        AutomationProperties.SetName(notes, "Информация о выпуске");
        var stage = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var bar = SettingsWindow.UpdateBar();
        var progressRow = new StackPanel { Spacing = 6 };
        progressRow.Children.Add(stage); progressRow.Children.Add(bar);
        var body = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8 };
        body.Children.Add(heading); Grid.SetRow(notes, 1); body.Children.Add(notes); Grid.SetRow(progressRow, 2); body.Children.Add(progressRow);
        var buttons = new DockPanel();
        var later = SettingsWindow.Command("Напомнить позже", () => dialog.Close(false)); later.IsCancel = true;
        var keep = library.KeepVersions; var repository = offeredRepository;
        CancellationTokenSource? running = null;
        OfferResult? result = null;
        Button download = null!;
        async Task Download()
        {
            if (running is not null || closed) return;
            using var cancel = new CancellationTokenSource();
            running = cancel; updateDownloading = true;
            later.IsEnabled = false; download.IsEnabled = false;
            stage.Text = new UpdateProgress(UpdateStage.Downloading).Text; stage.IsVisible = bar.IsVisible = true;
            var finished = false;
            var progress = new Progress<UpdateProgress>(p => { if (!finished) { stage.Text = p.Text; SettingsWindow.ShowUpdateProgress(bar, p); } });
            try
            {
                // Once the package is prepared the offer ends, also when the helper does not start: the package stays pending and
                // the next start of the program starts the helper (the notice says so); a retry would only meet the restart rule.
                result = new(await Task.Run(() =>
                {
                    try { return Program.CreateUpdater(repository, keep).StageAndLaunch(release, current, Updater.CurrentRuntime, Program.LightBuild,
                        Program.RestartArguments, progress, cancel.Token); }
                    finally { finished = true; }
                }), null);
            }
            catch (Exception ex) { result = new(null, ex); }
            finally { running = null; }
            dialog.Close(true);
        }
        download = SettingsWindow.Command("Загрузить", Download); download.IsDefault = true; download.FontWeight = FontWeight.Bold;
        dialog.Closing += (_, e) =>
        {
            if (running is not { } cancel) return;
            e.Cancel = true;
            if (!cancel.IsCancellationRequested) { cancel.Cancel(); stage.Text = "Отмена загрузки… Дождитесь её остановки."; }
        };
        DockPanel.SetDock(later, Dock.Left); DockPanel.SetDock(download, Dock.Right);
        later.HorizontalAlignment = HorizontalAlignment.Left; download.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Children.Add(later); buttons.Children.Add(download);
        dialog.Content = DialogLayout.Frame(body, buttons, scrollBody: false);
        typography.ApplyDialog(dialog);
        later.Margin = download.Margin = new Thickness(0);
        // The smallest window grows with the dialog font as the window itself: the heading and the buttons stay in view.
        dialog.MinWidth *= UiTypography.DialogScale(typography.DialogSize); dialog.MinHeight *= UiTypography.DialogScale(typography.DialogSize);
        dialog.Opened += (_, _) => download.Focus();
        updateOfferOpen = true;
        try { await dialog.ShowDialog<bool>(this); }
        finally { updateOfferOpen = false; updateDownloading = false; }
        // «Напомнить позже» (or the window closed): it does not come up by itself again in this session; the notice still opens it.
        if (result is null) { pendingUpdate.Settled(); if (!closed) RestoreListFocus(); return; }
        OfferDownloaded(release, result);
    }

    /// <summary>What «Загрузить» of the offer came to: the prepared release, or why not (a cancel is an OperationCanceledException).</summary>
    private sealed record OfferResult(StagedUpdate? Staged, Exception? Failure);

    private void OfferDownloaded(ReleaseInfo release, OfferResult result)
    {
        if (result.Staged is { } staged)
        {
            AppLog.Info(staged.HelperError is null ? "update.offer.staged" : "update.offer.helper_failed");
            offeredRelease = null;
            updateNotice = staged.Notice;
        }
        else if (result.Failure is OperationCanceledException)
        {
            // Nothing was prepared: the offer stays, a click on the notice opens the window again.
            AppLog.Info("update.offer.cancelled");
            // Short enough for the status line of a narrow window (07.11).
            updateNotice = $"Загрузка версии {release.Version} отменена";
        }
        else
        {
            AppLog.Warning("update.offer.failed", result.Failure);
            updateNotice = $"Версия {release.Version} не загружена: {LegacyImport.ErrorMessage(result.Failure ?? new InvalidOperationException())}";
        }
        if (closed) return;
        if (!reading && !writing) status.Text = SourceDescription();
        RestoreListFocus();
    }
}
