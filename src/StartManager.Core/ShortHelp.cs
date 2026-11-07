namespace StartManager.Core;

public sealed record ShortHelpLine(ShortcutCommand Command, string Action)
{
    /// <summary>The keys of the command from the shortcut table: «F8, Ctrl+D».</summary>
    public string Keys => string.Join(", ", Shortcuts.KeysOf(Command));
    public override string ToString() => $"{Keys} = {Action}";
}

public sealed record ShortHelpSection(string Title, IReadOnlyList<ShortHelpLine> Lines);

/// <summary>
/// «Краткая справка» (the original's FormShortHelp, Alt+F1): the original's three sections, the keys of БазоНавигатор.
/// Not carried over: F6 and Alt+F10/Alt+F11 (no such menus here), «Временная блокировка», Snegopat and «Проверить обновление
/// базы» (F9) — those functions are not ported.
/// </summary>
public static class ShortHelp
{
    public static IReadOnlyList<ShortHelpSection> Sections { get; } =
    [
        new("Работа со стартером",
        [
            new(ShortcutCommand.ShortHelp, "Краткая справка (это окно)"),
            new(ShortcutCommand.Search, "Поиск базы в списке"),
            new(ShortcutCommand.SearchByName, "Поиск по наименованию"),
            new(ShortcutCommand.SearchByConnection, "Поиск по строке соединения"),
            new(ShortcutCommand.GoToFavorites, "Перейти к разделу «Избранное»"),
            new(ShortcutCommand.Snapshots, "Снимки списка информационных баз"),
            new(ShortcutCommand.PlatformFilter, "Отбор по версии платформы"),
        ]),
        new("Запуск информационной базы",
        [
            new(ShortcutCommand.Launch, "Запустить 1С:Предприятие"),
            new(ShortcutCommand.LaunchWithParameters, "Запустить 1С:Предприятие с выбором параметров"),
            new(ShortcutCommand.LaunchWithLogin, "Запустить 1С:Предприятие с аутентификацией"),
            new(ShortcutCommand.Designer, "Запустить конфигуратор"),
            new(ShortcutCommand.DesignerWithParameters, "Запустить конфигуратор с выбором параметров"),
            new(ShortcutCommand.DesignerWithLogin, "Запустить конфигуратор с аутентификацией"),
        ]),
        new("Работа со списком баз 1С:Предприятие 8",
        [
            new(ShortcutCommand.Rename, "Изменить наименование информационной базы или группы"),
            new(ShortcutCommand.MoveToGroup, "Переместить в группу"),
            new(ShortcutCommand.AddBase, "Добавить информационную базу"),
            new(ShortcutCommand.DuplicateBase, "Дублировать текущую файловую информационную базу"),
            new(ShortcutCommand.AddGroup, "Создать новую группу"),
            new(ShortcutCommand.Delete, "Удалить из списка"),
            new(ShortcutCommand.Properties, "Свойства текущей информационной базы или группы"),
            new(ShortcutCommand.BaseInfo, "Сводная информация"),
            new(ShortcutCommand.Reload, "Перечитать список баз"),
            new(ShortcutCommand.ToggleFavorite, "Добавить базу в раздел «Избранное» или убрать из него"),
            new(ShortcutCommand.AssignVersion, "Назначить версию"),
            new(ShortcutCommand.SetLock, "Установить блокировку информационной базы"),
            new(ShortcutCommand.RemoveLock, "Разблокировать информационную базу"),
            new(ShortcutCommand.Backup, "Выгрузить резервную копию данных"),
            new(ShortcutCommand.RestoreBackup, "Загрузить данные из резервной копии"),
            new(ShortcutCommand.OpenFolder, "Открыть каталог информационной базы или группы"),
            new(ShortcutCommand.ChangeFolder, "Изменить каталог файловой информационной базы"),
            new(ShortcutCommand.CollapseAll, "Свернуть все группы"),
            new(ShortcutCommand.ExpandAll, "Развернуть все группы"),
        ]),
    ];

    /// <summary>The help as plain text: a section title, then «• keys = action» lines, a blank line between sections.</summary>
    public static string Text => string.Join(Environment.NewLine + Environment.NewLine,
        Sections.Select(s => string.Join(Environment.NewLine, s.Lines.Select(l => "• " + l).Prepend(s.Title))));
}
