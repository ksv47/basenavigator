using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StartManager.Core;

namespace StartManager;

public sealed partial class MainWindow
{
    private enum AgentAnswer { Cancel, Continue, ChangeVersion }

    /// <summary>«Перед запуском ИБ проверять версию Агента сервера» (CheckRAgent of the original, _Unit207.pas:39190–39339): before
    /// a server base with the setting starts, the client chosen for this start is compared with the server agent of this computer.
    /// The base to start (its version may have been changed here), or null when the user cancelled. Windows only; anything that
    /// cannot be determined (no client, no agent) lets the start go on, as in the original, and the start reports its own errors.</summary>
    private async Task<BaseEntry?> CheckServerAgent(BaseEntry entry, bool designer, LaunchOptions? options)
    {
        if (!OperatingSystem.IsWindows() || entry is not { Kind: ConnectionKind.Server, CheckServerAgent: true }) return entry;
        // One check at a time: a second start while the agents are looked for or the question is open waits for none (Codex audit 14.10).
        if (agentCheck) { status.Text = "Идёт проверка версии агента сервера перед запуском. Дождитесь её завершения."; return null; }
        agentCheck = true;
        try { return await CheckServerAgentOnce(entry, designer, options); }
        finally { agentCheck = false; }
    }

    private bool agentCheck;

    private async Task<BaseEntry?> CheckServerAgentOnce(BaseEntry entry, bool designer, LaunchOptions? options)
    {
        var snapshot = library; var client = session; var source = library.ListPath;
        // After every wait: the list may have been reread or the base changed meanwhile. The start is of the base as it is now, with
        // the same connection and platform, or none (Codex audit and re-check 14.10).
        const string Changed = "Запуск отменён: список или база изменены во время проверки версии агента сервера. Запустите базу ещё раз.";
        // The settings of the list that choose the client and the session client count too: changed during the scan, the version in
        // the question would not be the one that starts (Grok audit 14.10).
        BaseEntry? Current() => closed || loadFailed || reading || library.ListPath != source || session != client ||
            !PlatformCatalog.SameClientSettings(snapshot, library) ||
            library.Bases.FirstOrDefault(x => x.Id == entry.Id) is not { } now || now.Kind != entry.Kind || now.Location != entry.Location ||
            now.Executable != entry.Executable || !Equals(now.PlatformRule, entry.PlatformRule) || now.PlatformVersion != entry.PlatformVersion ||
            now.ClientType != entry.ClientType || now.ListArchitecture != entry.ListArchitecture ? null : now;
        Version? clientVersion = null, serverVersion = null;
        string? changeTo = null;
        var architecture = PlatformArchitecture.Any;
        try
        {
            (clientVersion, serverVersion, changeTo, architecture) = await Task.Run<(Version?, Version?, string?, PlatformArchitecture)>(() =>
            {
                var start = Core.Launcher.Build(snapshot, entry, designer, session: client, options: options);
                if (PlatformCatalog.Read(start.FileName) is not { } current) return (null, null, null, PlatformArchitecture.Any);
                // The bitness of the new version: the base's own when its rule has one, else that of the client about to start, as
                // the original's V8CnfUpdateX64Bit with the bitness of that client (_Unit207.pas:39323–39336; Grok audit 14.10).
                var bitness = entry.PlatformRule is { Architecture: not PlatformArchitecture.Any } rule ? rule.Architecture
                    : current.Architecture is PlatformArchitecture.X86 or PlatformArchitecture.X64 ? current.Architecture : PlatformArchitecture.Any;
                if (ServerAgents.Mismatch(current.Version, current.Architecture, OperatingSystem.IsWindows() ? ServerAgents.Find() : []) is not { } server)
                    return (current.Version, null, null, bitness);
                // «Изменить версию на клиенте» as «Назначить версию» does it, when a client of the server's version starts this base then.
                string? target = null;
                try
                {
                    var (changed, _) = PlatformVersions.Assign(snapshot, [entry.Id], new PlatformVersionChoice(server.ToString(), bitness));
                    var next = changed.Bases.First(x => x.Id == entry.Id);
                    if (PlatformCatalog.Read(Core.Launcher.Build(changed, next, designer, session: client, options: options).FileName)?.Version == server)
                        target = server.ToString();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { }
                return (current.Version, server, target, bitness);
            });
        }
        // The check is optional: whatever fails in it (no client, no access to the services) lets the start report its own errors.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warning("agent.check.skipped", ex);
            if (Current() is { } plain) return plain;
            if (!closed) status.Text = Changed; return null;
        }
        if (Current() is not { } found) { if (!closed) status.Text = Changed; return null; }
        if (clientVersion is null || serverVersion is null) return found;
        AppLog.Info("agent.check.mismatch");
        // The version of this start is chosen in «Запустить с параметрами», and a list that cannot be changed keeps the base's version.
        string? noChange = options?.Platform is not null ? "Версия выбрана для этого запуска в окне параметров: версия базы не меняется."
            : ReadOnlyList is not null || CommonOf(entry) is not null ? "Список баз только для чтения: версия базы не меняется."
            : changeTo is null ? $"Клиент версии {serverVersion} для этой базы на компьютере не найден." : null;
        var answer = await AskServerAgent(clientVersion, serverVersion, noChange);
        if (answer == AgentAnswer.Cancel || closed) { status.Text = "Запуск отменён: версии клиента и агента сервера отличаются."; return null; }
        if (Current() is not { } now) { if (!closed) status.Text = Changed; return null; }
        if (answer == AgentAnswer.Continue) return now;
        // As the original: the version of the base becomes the server's, for this start and the next ones.
        if (ReadOnlyList is not null) { status.Text = "Версия не изменена: список только для чтения. Запуск отменён."; return null; }
        var (next, _) = PlatformVersions.Assign(library, [entry.Id], new PlatformVersionChoice(changeTo!, architecture));
        if (!await Persist(next, required: [entry.Id])) return null;
        Refresh(entry.Id);
        AppLog.Info("agent.check.version_changed");
        return library.Bases.FirstOrDefault(x => x.Id == entry.Id);
    }

    // The original's question (sub_0080FD2C): Yes «Изменить версию на клиенте», No «Продолжить», Cancel «Отмена».
    private async Task<AgentAnswer> AskServerAgent(Version clientVersion, Version serverVersion, string? noChange)
    {
        var dialog = new Window { Title = "Подтверждение", Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Отличаются версии платформы клиента и сервера!", FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(DialogLayout.Label($"Версия клиента: {clientVersion}"));
        panel.Children.Add(DialogLayout.Label($"Версия сервера: {serverVersion}"));
        panel.Children.Add(DialogLayout.Label("Для продолжения выберите действие."));
        if (noChange is not null) panel.Children.Add(new TextBlock { Text = noChange, TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Muted });
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var change = new Button { Content = "Изменить версию на клиенте", IsEnabled = noChange is null };
        var go = new Button { Content = "Продолжить" };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        change.Click += (_, _) => dialog.Close(AgentAnswer.ChangeVersion);
        go.Click += (_, _) => dialog.Close(AgentAnswer.Continue);
        cancel.Click += (_, _) => dialog.Close(AgentAnswer.Cancel);
        buttons.Children.Add(change); buttons.Children.Add(go); buttons.Children.Add(cancel);
        dialog.Content = DialogLayout.Frame(panel, buttons);
        typography.ApplyDialog(dialog);
        dialog.Opened += (_, _) => (noChange is null ? change : go).Focus();
        var answer = await dialog.ShowDialog<AgentAnswer?>(this);
        return answer ?? AgentAnswer.Cancel;
    }
}
