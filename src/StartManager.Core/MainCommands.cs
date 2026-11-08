namespace StartManager.Core;

/// <summary>What the row selected in the main list is. A section row («ИЗБРАННОЕ», «ИНФОРМАЦИОННЫЕ БАЗЫ») is None; the section
/// «ОТСУТСТВУЮЩИЕ КОНФИГУРАЦИИ» is Orphans, one of its rows Orphan.</summary>
public enum SelectionKind { None, Base, Group, FavoriteShortcut, Orphan, Orphans }

/// <summary>The state the commands of the main window depend on. Own: the selected base is a record of the open list (not of a
/// common list), the selected group exists in the open list. Kind: the connection of the selected base (also of a favorite
/// shortcut). GroupFolder: the selected group has a folder in its properties. ClearingOrphans: a removal of the cache of missing
/// configurations is under way (Codex audit 08.11, P3).</summary>
public sealed record CommandState(bool Loaded, bool Reading, bool ReadOnly, SelectionKind Selection, bool Own,
    ConnectionKind? Kind = null, bool GroupFolder = false, bool ClearingOrphans = false)
{
    public bool Ready => Loaded && !Reading;
    /// <summary>A base is selected: a row of the list or its shortcut in «Избранное».</summary>
    public bool Entry => Selection is SelectionKind.Base or SelectionKind.FavoriteShortcut;
}

/// <summary>The commands of the main window's toolbar, menus and keys.</summary>
public enum MainCommand
{
    ShortHelp, Search, SearchByName, SearchByConnection, GoToFavorites, Snapshots, PlatformFilter, ServiceMenu,
    Launch, LaunchWithParameters, LaunchWithLogin, Designer, DesignerWithParameters, DesignerWithLogin,
    Rename, MoveToGroup, AddBase, DuplicateBase, AddGroup, Delete, Properties, BaseInfo, Reload, ToggleFavorite,
    AssignVersion, SetLock, RemoveLock, Backup, RestoreBackup, OpenFolder, ChangeFolder, CollapseAll, ExpandAll,
    ClearCache, PropertiesMenu, SnapshotsMenu
}

/// <summary>Why a command is not available, for the status line when its key is pressed (<see cref="MainCommands.Refusal"/>).</summary>
public enum CommandRefusal
{
    None, SelectBase, ChooseBase, Busy, ReadOnlyList, CommonBase, CommonGroup, WebDesigner, FileFolder, FileFolderChange, FileLock,
    SetLockKind, BackupKind, GroupFolder
}

/// <summary>One command: Caption for a menu item, Hint for a toolbar button (both the original's where it has the command),
/// the command of the key table and the rule of availability.</summary>
public sealed record MainCommandInfo(MainCommand Command, string Caption, string Hint, ShortcutCommand? Shortcut, Func<CommandState, bool> CanExecute);

/// <summary>
/// The one table of the main window's commands: a toolbar button, a menu item and a key of one command take their caption, key and
/// availability from here. The rules follow what the actions themselves accept (their own checks stay as a guard): nothing is
/// offered that the action refuses. A read-only list keeps what does not write it: the properties (local settings are saved,
/// a change of the list is refused on saving), the folder of a group, the favorites and the cache of missing configurations
/// (kept in the profile and the cache folders, not in the .v8i).
/// </summary>
public static class MainCommands
{
    private static bool Always(CommandState _) => true;
    private static bool Ready(CommandState s) => s.Ready;
    private static bool Base(CommandState s) => s.Ready && s.Entry;
    private static bool OwnBase(CommandState s) => Base(s) && s.Own;
    private static bool ChangesOwnBase(CommandState s) => OwnBase(s) && !s.ReadOnly;
    private static bool OwnGroup(CommandState s) => s.Selection == SelectionKind.Group && s.Own;
    private static bool NotWeb(CommandState s) => Base(s) && s.Kind != ConnectionKind.Web;

    public static IReadOnlyList<MainCommandInfo> All { get; } =
    [
        new(MainCommand.ShortHelp, "Краткая справка", "Краткая справка", ShortcutCommand.ShortHelp, Always),
        new(MainCommand.Search, "Поиск базы", "Поиск / отбор по наименованию", ShortcutCommand.Search, Always),
        new(MainCommand.SearchByName, "Поиск по наименованию", "Поиск по наименованию", ShortcutCommand.SearchByName, Always),
        new(MainCommand.SearchByConnection, "Поиск по строке соединения", "Поиск по строке соединения", ShortcutCommand.SearchByConnection, Always),
        new(MainCommand.GoToFavorites, "Перейти к Избранному", "Перейти к Избранному", ShortcutCommand.GoToFavorites, Always),
        new(MainCommand.Snapshots, "Снимки информационных баз…", "Сохраненные снимки инф. баз", ShortcutCommand.Snapshots, Ready),
        // The original's «Выбрать платформу» (actV8SelectPlatform); here the filter by the branch (decision 12.10), so its own text.
        new(MainCommand.PlatformFilter, "Отбор по версии платформы", "Отбор по версии платформы", ShortcutCommand.PlatformFilter, Always),
        new(MainCommand.ServiceMenu, "Сервис", "Сервисное меню", ShortcutCommand.ServiceMenu, Always),
        new(MainCommand.Launch, "1С:Предприятие", "Запустить 1С:Предприятие", ShortcutCommand.Launch, Base),
        new(MainCommand.LaunchWithParameters, "Запустить с выбором параметров", "Запустить 1С:Предприятие с выбором параметров", ShortcutCommand.LaunchWithParameters, Base),
        new(MainCommand.LaunchWithLogin, "Запустить с аутентификацией", "Запустить 1С:Предприятие с аутентификацией", ShortcutCommand.LaunchWithLogin, Base),
        // The configurator does not connect through a web server.
        new(MainCommand.Designer, "Конфигуратор", "Запустить конфигуратор", ShortcutCommand.Designer, NotWeb),
        new(MainCommand.DesignerWithParameters, "Запустить с выбором параметров", "Запустить конфигуратор с выбором параметров", ShortcutCommand.DesignerWithParameters, NotWeb),
        new(MainCommand.DesignerWithLogin, "Запустить с аутентификацией", "Запустить конфигуратор с аутентификацией", ShortcutCommand.DesignerWithLogin, NotWeb),
        // A group on a read-only list keeps its dialog: the name is read-only there, the folder (a local setting) changes.
        new(MainCommand.Rename, "Изменить наименование…", "Изменить наименование", ShortcutCommand.Rename,
            s => s.Ready && (OwnGroup(s) || s.Entry && s.Own && !s.ReadOnly)),
        new(MainCommand.MoveToGroup, "Переместить в группу…", "Переместить в группу", ShortcutCommand.MoveToGroup, ChangesOwnBase),
        new(MainCommand.AddBase, "Создать информационную базу…", "Зарегистрировать информационную базу", ShortcutCommand.AddBase, s => s.Ready && !s.ReadOnly),
        new(MainCommand.DuplicateBase, "Дублировать информационную базу…", "Дублировать информационную базу", ShortcutCommand.DuplicateBase,
            s => ChangesOwnBase(s) && s.Kind == ConnectionKind.File),
        new(MainCommand.AddGroup, "Создать новую группу…", "Создать новую группу", ShortcutCommand.AddGroup, s => s.Ready && !s.ReadOnly),
        // A favorite shortcut leaves «Избранное», the cache of a missing configuration is removed: neither writes the list.
        new(MainCommand.Delete, "Удалить из списка…", "Удалить из списка", ShortcutCommand.Delete, s => s.Ready && s.Selection switch
        {
            SelectionKind.FavoriteShortcut => true,
            // One removal of the cache at a time: the next waits for it (a key then stays silent, as the action did).
            SelectionKind.Orphan or SelectionKind.Orphans => !s.ClearingOrphans,
            SelectionKind.Base or SelectionKind.Group => s.Own && !s.ReadOnly,
            _ => false
        }),
        // As the original's actProperty: a base or a group. A base of a common list keeps its own «Дополнительные параметры».
        new(MainCommand.Properties, "Свойства информационной базы…", "Свойства инф. базы", ShortcutCommand.Properties,
            s => s.Ready && (s.Entry || OwnGroup(s))),
        new(MainCommand.BaseInfo, "Сводная информация…", "Сводная информация", ShortcutCommand.BaseInfo, Base),
        new(MainCommand.Reload, "Перечитать список баз", "Перечитать список баз", ShortcutCommand.Reload, Always),
        // Favorites are kept in the profile: a read-only list keeps them.
        new(MainCommand.ToggleFavorite, "Добавить в избранное", "Добавить в избранное", ShortcutCommand.ToggleFavorite, Base),
        new(MainCommand.AssignVersion, "Назначить версию…", "Назначить версию", ShortcutCommand.AssignVersion,
            s => s.Ready && !s.ReadOnly && s.Own && (s.Entry || s.Selection == SelectionKind.Group)),
        // Setting a lock opens the properties on their page «Блокировка» (a common base too); removing deletes the lock file.
        new(MainCommand.SetLock, "Установить блокировку информационной базы…", "Установить блокировку информационной базы", ShortcutCommand.SetLock,
            s => Base(s) && s.Kind == ConnectionKind.File),
        new(MainCommand.RemoveLock, "Разблокировать информационную базу…", "Разблокировать информационную базу", ShortcutCommand.RemoveLock,
            s => OwnBase(s) && s.Kind == ConnectionKind.File),
        new(MainCommand.Backup, "Выгрузить резервную копию данных…", "Выгрузить резервную копию данных", ShortcutCommand.Backup,
            s => Base(s) && s.Kind is ConnectionKind.File or ConnectionKind.Server),
        new(MainCommand.RestoreBackup, "Загрузить данные из резервной копии…", "Загрузить данные из резервной копии", ShortcutCommand.RestoreBackup,
            s => Base(s) && s.Kind is ConnectionKind.File or ConnectionKind.Server),
        // Opening a folder reads nothing of the list.
        new(MainCommand.OpenFolder, "Открыть каталог", "Открыть каталог", ShortcutCommand.OpenFolder,
            s => s.Entry && s.Kind == ConnectionKind.File || s.Selection == SelectionKind.Group && s.GroupFolder),
        new(MainCommand.ChangeFolder, "Изменить каталог", "Изменить каталог", ShortcutCommand.ChangeFolder,
            s => ChangesOwnBase(s) && s.Kind == ConnectionKind.File),
        new(MainCommand.CollapseAll, "Свернуть все", "Свернуть все", ShortcutCommand.CollapseAll, Always),
        new(MainCommand.ExpandAll, "Развернуть все", "Развернуть все", ShortcutCommand.ExpandAll, Always),
        // The cache panel works for the selected base or for all bases of the list.
        new(MainCommand.ClearCache, "Очистить временные файлы…", "Очистка временных файлов", null, Ready),
        new(MainCommand.PropertiesMenu, "Меню свойств", "Меню свойств", null, Ready),
        new(MainCommand.SnapshotsMenu, "Меню снимков", "Меню снимков", null, Ready),
    ];

    private static readonly Dictionary<MainCommand, MainCommandInfo> ByCommand = All.ToDictionary(x => x.Command);
    private static readonly Dictionary<ShortcutCommand, MainCommand> ByShortcut =
        All.Where(x => x.Shortcut is not null).ToDictionary(x => x.Shortcut!.Value, x => x.Command);

    public static MainCommandInfo Info(MainCommand command) => ByCommand[command];
    public static bool CanExecute(MainCommand command, CommandState state) => ByCommand[command].CanExecute(state);
    public static string Caption(MainCommand command) => ByCommand[command].Caption;
    public static string Hint(MainCommand command) => ByCommand[command].Hint;

    /// <summary>The command of a key of the table.</summary>
    public static MainCommand Of(ShortcutCommand shortcut) => ByShortcut[shortcut];

    /// <summary>The first key of the command in <see cref="Shortcuts"/>, or null.</summary>
    public static Shortcut? Key(MainCommand command) =>
        ByCommand[command].Shortcut is { } shortcut ? Shortcuts.KeysOf(shortcut).FirstOrDefault() : null;

    /// <summary>The key as the original shows it (<see cref="Shortcut.ToString"/>): Shift+Ctrl+Alt+, «Ins», «Del».</summary>
    public static string? KeyText(MainCommand command) => Key(command)?.ToString();

    /// <summary>The original's caption of its favorite item on a node of «Избранное» (pbV8ContextItemPopup): removing the shortcut.</summary>
    public const string FavoriteRemoveText = "Удалить из избранных";

    /// <summary>The caption and the hint for the selected row: Delete on a favorite shortcut removes it from «Избранное» and says so,
    /// on the toolbar, in the menu and by Del alike (Grok audit 09.11, P2).</summary>
    public static string Caption(MainCommand command, CommandState state) =>
        command == MainCommand.Delete && state.Selection == SelectionKind.FavoriteShortcut ? FavoriteRemoveText : Caption(command);
    public static string Hint(MainCommand command, CommandState state) =>
        command == MainCommand.Delete && state.Selection == SelectionKind.FavoriteShortcut ? FavoriteRemoveText : Hint(command);
    public static string Tip(MainCommand command, CommandState state) =>
        KeyText(command) is { } key ? $"{Hint(command, state)} · {key}" : Hint(command, state);

    public const string SelectBaseText = "Сначала выберите базу в списке.";
    public const string ChooseBaseText = "Выберите информационную базу.";
    public const string BusyText = "Список занят или не загружен. Дождитесь завершения операции.";
    public const string WebDesignerText = "Конфигуратор не подключается к базе на веб-сервере. Откройте её в 1С:Предприятии или в браузере.";
    public const string FileFolderText = "Каталог есть только у файловой базы.";
    public const string FileFolderChangeText = "Каталог меняется только у файловой базы.";
    public const string FileLockText = "Снятие блокировки возможно только для файлового варианта информационной базы.";
    /// <summary>The text of the properties' page «Блокировка» for a base that is not a file one.</summary>
    public const string SetLockKindText = "Блокировка доступна только для файлового варианта информационной базы.";
    public const string BackupKindText = "Резервное копирование и загрузка доступны только для файловых и серверных информационных баз.";

    /// <summary>
    /// Why a key of an unavailable command is refused, as the actions said it before the table (decision of the user 08.11.2026):
    /// None — the key stays silent, as it was (the list is being read, nothing suitable is selected). A key of the list never says
    /// SelectBase: those keys were not handled without a base. The buttons and menu items are only turned off.
    /// </summary>
    public static CommandRefusal Refusal(MainCommand command, CommandState s)
    {
        if (CanExecute(command, s)) return CommandRefusal.None;
        var group = s.Selection == SelectionKind.Group;
        // The order of the actions' own checks: the read-only list first, then the common list.
        CommandRefusal Change(bool entry, bool groupToo) =>
            entry ? s.ReadOnly ? CommandRefusal.ReadOnlyList : !s.Own ? CommandRefusal.CommonBase : CommandRefusal.None
            : groupToo && group ? s.ReadOnly ? CommandRefusal.ReadOnlyList : !s.Own ? CommandRefusal.CommonGroup : CommandRefusal.None
            : CommandRefusal.None;
        // A command that does not open while the list is busy said nothing then; the backup said it was busy.
        if (!s.Ready && command is not (MainCommand.Backup or MainCommand.RestoreBackup or MainCommand.OpenFolder)) return CommandRefusal.None;
        return command switch
        {
            MainCommand.Launch or MainCommand.LaunchWithLogin or MainCommand.LaunchWithParameters or MainCommand.ToggleFavorite =>
                s.Entry ? CommandRefusal.None : CommandRefusal.SelectBase,
            MainCommand.Designer or MainCommand.DesignerWithLogin or MainCommand.DesignerWithParameters =>
                !s.Entry ? CommandRefusal.SelectBase : s.Kind == ConnectionKind.Web ? CommandRefusal.WebDesigner : CommandRefusal.None,
            MainCommand.Rename => s.Entry ? Change(true, false) : group && !s.Own ? CommandRefusal.CommonGroup : CommandRefusal.None,
            MainCommand.MoveToGroup => Change(s.Entry, false),
            MainCommand.AddBase or MainCommand.AddGroup => s.ReadOnly ? CommandRefusal.ReadOnlyList : CommandRefusal.None,
            // The list's Shift+Ins is a key of a file base only: another base said nothing.
            MainCommand.DuplicateBase => s.Entry && s.Kind == ConnectionKind.File ? Change(true, false) : CommandRefusal.None,
            MainCommand.Delete => Change(s.Selection == SelectionKind.Base, true),
            MainCommand.Properties => group && !s.Own ? CommandRefusal.CommonGroup : CommandRefusal.None,
            MainCommand.BaseInfo => s.Entry ? CommandRefusal.None : CommandRefusal.ChooseBase,
            MainCommand.AssignVersion => Change(s.Entry, true),
            // Ctrl+Alt+L on a server or web base opened the properties on «Блокировка», which said only this (decision 08.11).
            MainCommand.SetLock => s.Entry ? CommandRefusal.SetLockKind : CommandRefusal.None,
            MainCommand.RemoveLock => !s.Entry ? CommandRefusal.None : s.Kind != ConnectionKind.File ? CommandRefusal.FileLock : CommandRefusal.CommonBase,
            MainCommand.Backup or MainCommand.RestoreBackup => !s.Ready ? CommandRefusal.Busy : !s.Entry ? CommandRefusal.SelectBase : CommandRefusal.BackupKind,
            MainCommand.OpenFolder => group ? CommandRefusal.GroupFolder : s.Entry ? CommandRefusal.FileFolder : CommandRefusal.None,
            MainCommand.ChangeFolder => !s.Entry ? CommandRefusal.None : s.Kind != ConnectionKind.File ? CommandRefusal.FileFolderChange : Change(true, false),
            _ => CommandRefusal.None
        };
    }

    /// <summary>The text of a refusal that does not depend on the selected record; null for those the window words itself (the
    /// reason of the read-only list, the common list, the group).</summary>
    public static string? RefusalText(CommandRefusal refusal) => refusal switch
    {
        CommandRefusal.SelectBase => SelectBaseText,
        CommandRefusal.ChooseBase => ChooseBaseText,
        CommandRefusal.Busy => BusyText,
        CommandRefusal.WebDesigner => WebDesignerText,
        CommandRefusal.FileFolder => FileFolderText,
        CommandRefusal.FileFolderChange => FileFolderChangeText,
        CommandRefusal.FileLock => FileLockText,
        CommandRefusal.SetLockKind => SetLockKindText,
        CommandRefusal.BackupKind => BackupKindText,
        _ => null
    };

    /// <summary>The tooltip of a toolbar button: the hint and the key.</summary>
    public static string Tip(MainCommand command) => KeyText(command) is { } key ? $"{Hint(command)} · {key}" : Hint(command);
}
