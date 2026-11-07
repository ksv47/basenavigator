using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class AboutHelpTests
{
    [Fact]
    public void Help_lists_every_handled_command_once_and_only_handled_keys()
    {
        var listed = ShortHelp.Sections.SelectMany(s => s.Lines).Select(l => l.Command).ToList();
        Assert.Equal(listed.Count, listed.Distinct().Count());
        // Every command of the shortcut table (which the window dispatches by) is in the help, and nothing else is.
        Assert.Equal(Enum.GetValues<ShortcutCommand>().OrderBy(x => x), listed.OrderBy(x => x));
        Assert.All(Enum.GetValues<ShortcutCommand>(), c => Assert.NotEmpty(Shortcuts.KeysOf(c)));
    }

    [Fact]
    public void A_key_has_one_command_and_the_window_never_shadows_the_list()
    {
        var keys = Shortcuts.All.Select(x => x.Shortcut).ToList();
        // The window's handler runs first (tunnel): the same key in both scopes would never reach the list.
        Assert.Equal(keys.Count, keys.Distinct().Count());
        // The window parses the names as Avalonia keys: only function keys, letters, Insert, Delete and Enter are used.
        Assert.All(keys, k => Assert.Matches("^(F([1-9]|1[0-2])|[A-Z]|Insert|Delete|Enter)$", k.Key));
    }

    [Theory]
    [InlineData(ShortcutScope.Window, "F1", ShortcutModifiers.None, ShortcutCommand.ShortHelp)]
    [InlineData(ShortcutScope.Window, "F1", ShortcutModifiers.Alt, ShortcutCommand.ShortHelp)]
    [InlineData(ShortcutScope.List, "F3", ShortcutModifiers.None, ShortcutCommand.Launch)]
    [InlineData(ShortcutScope.List, "F4", ShortcutModifiers.Shift, ShortcutCommand.DesignerWithLogin)]
    [InlineData(ShortcutScope.Window, "F3", ShortcutModifiers.Ctrl, ShortcutCommand.LaunchWithParameters)]
    [InlineData(ShortcutScope.Window, "F8", ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, ShortcutCommand.AssignVersion)]
    [InlineData(ShortcutScope.List, "F8", ShortcutModifiers.None, ShortcutCommand.ToggleFavorite)]
    [InlineData(ShortcutScope.Window, "D", ShortcutModifiers.Ctrl, ShortcutCommand.ToggleFavorite)]
    [InlineData(ShortcutScope.Window, "Enter", ShortcutModifiers.Alt, ShortcutCommand.Properties)]
    [InlineData(ShortcutScope.List, "Insert", ShortcutModifiers.Shift, ShortcutCommand.DuplicateBase)]
    [InlineData(ShortcutScope.Window, "F11", ShortcutModifiers.None, ShortcutCommand.CollapseAll)]
    [InlineData(ShortcutScope.Window, "F11", ShortcutModifiers.Shift, ShortcutCommand.ChangeFolder)]
    public void Keys_find_their_commands(ShortcutScope scope, string key, ShortcutModifiers modifiers, ShortcutCommand command) =>
        Assert.Equal(command, Shortcuts.Find(scope, new Shortcut(key, modifiers)));

    [Fact]
    public void Keys_of_another_scope_or_with_extra_modifiers_find_nothing()
    {
        Assert.Null(Shortcuts.Find(ShortcutScope.Window, new Shortcut("F3")));
        Assert.Null(Shortcuts.Find(ShortcutScope.List, new Shortcut("F1")));
        Assert.Null(Shortcuts.Find(ShortcutScope.Window, new Shortcut("F", ShortcutModifiers.Ctrl | ShortcutModifiers.Shift | ShortcutModifiers.Alt)));
        Assert.Null(Shortcuts.Find(ShortcutScope.Window, new Shortcut("F6")));
    }

    [Fact]
    public void Help_text_keeps_the_original_sections_without_functions_not_carried_over()
    {
        Assert.Equal(["Работа со стартером", "Запуск информационной базы", "Работа со списком баз 1С:Предприятие 8"],
            ShortHelp.Sections.Select(s => s.Title));
        var text = ShortHelp.Text;
        Assert.Contains("• F1, Alt+F1 = Краткая справка (это окно)", text);
        Assert.Contains("• F3 = Запустить 1С:Предприятие", text);
        Assert.Contains("• Ctrl+F4 = Запустить конфигуратор с выбором параметров", text);
        Assert.Contains("• F8, Ctrl+D = ", text);
        Assert.Contains("• Ctrl+Shift+F8 = Назначить версию", text);
        Assert.Contains("• Ctrl+Alt+I = Сводная информация", text);
        foreach (var missing in new[] { "Snegopat", "Временная блокировка", "F6", "F9", "Alt+F10", "Alt+F11" })
            Assert.DoesNotContain(missing, text);
        Assert.StartsWith("Работа со стартером" + Environment.NewLine + "• F1", text);
    }

    [Fact]
    public void Shortcut_text_orders_the_modifiers()
    {
        Assert.Equal("Ctrl+Alt+Shift+F", new Shortcut("F", ShortcutModifiers.Shift | ShortcutModifiers.Alt | ShortcutModifiers.Ctrl).ToString());
        Assert.Equal("Insert", new Shortcut("Insert").ToString());
    }

    [Fact]
    public void Launch_keys_are_shown_as_passed_or_named_absent()
    {
        Assert.Equal("<не передавались>", AboutInfo.LaunchKeys([]));
        Assert.Equal("--portable --settings \"C:\\My Bases\\library.json\"", AboutInfo.LaunchKeys(["--portable", "--settings", "C:\\My Bases\\library.json"]));
        Assert.Equal("\"\" \"a\\\"b\"", AboutInfo.LaunchKeys(["", "a\"b"]));
    }

    [Fact]
    public void Storage_names_the_portable_normal_or_explicit_profile()
    {
        var app = Path.Combine(Path.GetTempPath(), "bn-app");
        var portable = Path.Combine(app, "Settings");
        var normal = Path.Combine(Path.GetTempPath(), "bn-user", "BaseNavigator");
        Assert.Equal(StorageKind.Portable, AboutInfo.Storage(Path.Combine(portable, "library.json"), portable, normal, OperatingSystem.IsWindows()));
        Assert.Equal(StorageKind.Normal, AboutInfo.Storage(Path.Combine(normal, "library.json"), portable, normal + Path.DirectorySeparatorChar, OperatingSystem.IsWindows()));
        Assert.Equal(StorageKind.Explicit, AboutInfo.Storage(Path.Combine(normal, "other.json"), portable, normal, OperatingSystem.IsWindows()));
        Assert.Equal(StorageKind.Explicit, AboutInfo.Storage(Path.Combine(app, "library.json"), portable, normal, OperatingSystem.IsWindows()));
        // Windows compares the folder without case, Linux with it.
        Assert.Equal(StorageKind.Portable, AboutInfo.Storage(Path.Combine(portable.ToUpperInvariant(), "library.json"), portable, normal, true));
        Assert.Equal(StorageKind.Explicit, AboutInfo.Storage(Path.Combine(portable.ToUpperInvariant(), "library.json"), portable, normal, false));
        Assert.StartsWith("Портативный", AboutInfo.StorageText(StorageKind.Portable));
        Assert.StartsWith("Обычный", AboutInfo.StorageText(StorageKind.Normal));
        Assert.Contains("--settings", AboutInfo.StorageText(StorageKind.Explicit));
    }

    [Fact]
    public void Build_size_and_duration_are_formatted_as_the_original()
    {
        Assert.Equal("05.03.2026", AboutInfo.BuildDate(new DateTime(2026, 3, 5, 23, 59, 0)));
        Assert.Equal("47,3 МБ (49 612 345 байт)", AboutInfo.FileSize(49_612_345));
        Assert.Equal("512 КБ (524 288 байт)", AboutInfo.FileSize(524_288));
        Assert.Equal("752 КБ (770 048 байт), BaseNavigator.dll", AboutInfo.FileSize(770_048, "BaseNavigator.dll"));
        Assert.Equal("00:00:07", AboutInfo.Duration(TimeSpan.FromSeconds(7)));
        Assert.Equal("23:59:59", AboutInfo.Duration(new TimeSpan(23, 59, 59)));
        Assert.Equal("3 дн. 04:05:06", AboutInfo.Duration(new TimeSpan(3, 4, 5, 6, 900)));
        Assert.Equal("00:00:00", AboutInfo.Duration(TimeSpan.FromSeconds(-5)));
        Assert.Equal("Windows x64 (автономный вариант)", AboutInfo.CompiledFor("Windows", Architecture.X64, false));
        Assert.Equal("Linux arm64 (лёгкий вариант, нужен .NET 10)", AboutInfo.CompiledFor("Linux", Architecture.Arm64, true));
    }

    [Fact]
    public void System_names_come_from_the_registry_or_os_release()
    {
        Assert.Equal("Windows 10 Enterprise LTSC 2021 21H2 (10.0.19044)",
            AboutInfo.WindowsName("Windows 10 Enterprise LTSC 2021", "21H2", new Version(10, 0, 19044, 0)));
        Assert.Equal("Windows 11 Pro 24H2 (10.0.26100)", AboutInfo.WindowsName("Windows 10 Pro", "24H2", new Version(10, 0, 26100)));
        Assert.Equal("Windows (10.0.19045)", AboutInfo.WindowsName(null, " ", new Version(10, 0, 19045)));
        Assert.Equal("Ubuntu 24.04.1 LTS", AboutInfo.LinuxName("NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nID=ubuntu\n", "Linux 6.8"));
        Assert.Equal("Linux 6.8.0", AboutInfo.LinuxName(null, " Linux 6.8.0 "));
        Assert.Equal("Linux 6.8.0", AboutInfo.LinuxName("PRETTY_NAME=\"\"", "Linux 6.8.0"));
        Assert.Equal("Ubuntu 24.04, x64 · .NET 10.0.1", AboutInfo.SystemLine("Ubuntu 24.04", Architecture.X64, ".NET 10.0.1 "));
        Assert.Equal("Запущено без прав администратора", AboutInfo.Privileges(true, false));
        Assert.Equal("Запущено от имени администратора", AboutInfo.Privileges(true, true));
        Assert.Equal("Запущено от имени root", AboutInfo.Privileges(false, true));
    }

    [Fact]
    public void Update_offer_texts_and_when_it_comes_up_by_itself()
    {
        Assert.Equal("Доступна версия 0.3.0.8 (сейчас установлена 0.3.0.7). Хотите загрузить новую версию?",
            UpdateOffer.Message(new Version(0, 3, 0, 8), new Version(0, 3, 0, 7)));
        Assert.Equal("Описание выпуска не найдено.", UpdateOffer.Notes(null));
        Assert.Equal("Описание выпуска не найдено.", UpdateOffer.Notes(" \r\n "));
        Assert.Equal("- Первое\n- Второе", UpdateOffer.Notes("\r\n- Первое\r\n- Второе\r\n"));
        Assert.True(UpdateOffer.ShowsItself(UpdateMode.Notify));
        Assert.False(UpdateOffer.ShowsItself(UpdateMode.Download));
        Assert.False(UpdateOffer.ShowsItself(UpdateMode.Off));
    }

    // Grok full audit 24.10, P2-3: a failed automatic check was only logged (without its text). Its status line is the text of
    // «Проверить сейчас»; a failure is published like a found release, without the offer window.
    [Fact]
    public void A_failed_automatic_check_is_said_in_the_status_line()
    {
        Assert.Equal("Автоматическая проверка обновлений не выполнена: GitHub CLI не выполнил вход. Выполните «gh auth login» и повторите.",
            UpdateOffer.CheckFailed(GitHubReleaseSource.Failure(1, "To get started with GitHub CLI, please run:  gh auth login")));
        Assert.Contains("GitHub не ответил вовремя", UpdateOffer.CheckFailed(new TimeoutException("GitHub не ответил вовремя. Проверьте подключение и повторите.")));
        Assert.Contains("SHA256SUMS.txt", UpdateOffer.CheckFailed(new InvalidDataException("В выпуске нет SHA256SUMS.txt.")));
        var pending = new PendingUpdateNotice();
        pending.Found(offers: false);
        Assert.Equal((true, false), pending.Take(busy: false, otherWindowOpen: false));
        Assert.False(pending.Waiting);
    }

    [Fact]
    public void A_result_found_while_busy_is_published_once_the_program_is_idle()
    {
        // Grok audit 15.10: the check finished while the list was still read.
        var pending = new PendingUpdateNotice();
        pending.Found(offers: true);
        Assert.Equal((false, false), pending.Take(busy: true, otherWindowOpen: false));
        Assert.True(pending.Waiting);
        Assert.Equal((true, true), pending.Take(busy: false, otherWindowOpen: false));
        Assert.False(pending.Waiting);
        Assert.Equal((false, false), pending.Take(busy: false, otherWindowOpen: false));
    }

    [Fact]
    public void The_offer_waits_for_another_window_and_comes_up_once()
    {
        var pending = new PendingUpdateNotice();
        pending.Found(offers: true);
        // The notice is published at once; the offer waits until the other window closes.
        Assert.Equal((true, false), pending.Take(busy: false, otherWindowOpen: true));
        Assert.Equal((false, false), pending.Take(busy: false, otherWindowOpen: true));
        Assert.Equal((false, true), pending.Take(busy: false, otherWindowOpen: false));
        Assert.Equal((false, false), pending.Take(busy: false, otherWindowOpen: false));
    }

    [Fact]
    public void The_offer_does_not_come_up_after_it_was_settled_or_in_the_download_way()
    {
        var opened = new PendingUpdateNotice();
        opened.Found(offers: true);
        Assert.Equal((true, false), opened.Take(busy: false, otherWindowOpen: true));
        opened.Settled(); // opened from the notice or «Напомнить позже»
        Assert.Equal((false, false), opened.Take(busy: false, otherWindowOpen: false));
        Assert.False(opened.Waiting);
        var download = new PendingUpdateNotice();
        download.Found(offers: false);
        Assert.Equal((true, false), download.Take(busy: false, otherWindowOpen: false));
        Assert.False(download.Waiting);
    }

    [Fact]
    public void The_about_link_is_the_repository_the_updater_reads()
    {
        Assert.Equal("https://github.com/ksv47/basenavigator", AboutInfo.RepositoryUrl);
        Assert.Equal("github.com/ksv47/basenavigator", AboutInfo.RepositoryCaption);
        var url = new Uri(AboutInfo.RepositoryUrl);
        Assert.Equal((Uri.UriSchemeHttps, "github.com"), (url.Scheme, url.Host));
        // The default source of updates and the link name one repository.
        Assert.Equal("/" + new GitHubReleaseSource().Repository, url.AbsolutePath);
        Assert.True(GitHubReleaseSource.IsRepository(GitHubReleaseSource.DefaultRepository));
    }

    [Fact]
    public void The_repository_name_is_written_once_in_the_program()
    {
        // A second literal would let the link and the updater part when the repository moves.
        var src = Path.GetDirectoryName(Path.GetDirectoryName(SourceFile()))!;
        var holders = new[] { "StartManager", "StartManager.Core" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(src, project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(file => File.ReadAllText(file).Contains("ksv47/basenavigator", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).ToList();
        Assert.Equal(["Updater.cs"], holders);
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
