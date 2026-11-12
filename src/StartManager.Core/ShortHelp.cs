namespace StartManager.Core;

public sealed record ShortHelpLine(ShortcutCommand Command, string Action)
{
    /// <summary>The keys of the command from the shortcut table: «F8, Ctrl+D».</summary>
    public string Keys => string.Join(", ", Shortcuts.KeysOf(Command));
    public override string ToString() => $"{Keys} = {Action}";
}

public sealed record ShortHelpSection(string Title, IReadOnlyList<ShortHelpLine> Lines);

/// <summary>A section of explanations after the keys: the conditions of the settings, moved from the pages of «Настройки программы»
/// into the help as the original keeps its forms nearly free of text (settings look 11.11.2026).</summary>
public sealed record ShortHelpNote(string Title, IReadOnlyList<string> Lines);

/// <summary>
/// «Краткая справка» (the original's FormShortHelp, Alt+F1): the original's three sections, the keys of БазоНавигатор.
/// Not carried over: F6 and Alt+F11 (no such menus here), «Временная блокировка», Snegopat and «Проверить обновление
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
            new(ShortcutCommand.ServiceMenu, "Сервисное меню"),
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

    /// <summary>«Настройки программы»: what the pages no longer say under their fields (their tooltips say it too).</summary>
    public static IReadOnlyList<ShortHelpNote> DialogNotes { get; } =
    [
        new("Настройки программы",
        [
            "Изменения сохраняются кнопками «Применить» и «Сохранить»; «Закрыть» отменяет неприменённое. Сценарии резервирования " +
                "и ярлык в меню приложений записываются сразу своими командами.",
            "Общее: окно сворачивается или закрывается после успешного создания процесса 1С и сохранения истории; при ошибке или " +
                "несохранённых настройках оно остаётся открытым. Запрет второго экземпляра действует для одного файла настроек " +
                "и применяется после перезапуска всех открытых экземпляров; ярлыки запуска баз продолжают работать.",
            "«1С:Стартер» открывает штатное окно 1С со своим списком баз: для платформы выбранной базы, а «▾» — по версии.",
            "Платформа 1С: этот клиент используется по умолчанию, индивидуальный клиент базы задаётся в её свойствах. Подбор версии: " +
                "8.3 или 8.3.27 — новейшая подходящая версия, полный номер — точное совпадение; при отсутствии совпадения запуск " +
                "отменяется. При предпочтении разрядности сначала выбирается новейший релиз; «Автоматически» предпочитает x86, как 1С. " +
                "Пути поиска: заданные каталоги просматриваются до четырёх уровней вглубь, стандартные каталоги ищутся всегда.",
            "Настройки платформы: для баз ветки, у которых в свойствах не задан свой клиент или своя версия. Версия ветки не заменяет " +
                "полный номер сборки из списка; исполняемый файл ветки применяется ко всем её базам. Пусто — общая настройка «Платформа 1С». " +
                "Индивидуальное правило базы имеет приоритет; версию в самом списке .v8i меняет «Назначить версию» (Shift+Ctrl+F8).",
            "Версии платформы: «Основная версия» — версия, по которой запускаются базы без версии в списке, и ветка, чьи «Настройки " +
                "платформы» к ним применяются; «Как в настройках 1CEStart» — версия по умолчанию стартера 1С. «Каталог информационных " +
                "баз» предлагается для размещения новой файловой базы.",
            "Списки баз: пустое поле — локальный список БазоНавигатора. Переключение происходит после сохранения, подключения прежнего " +
                "источника сохраняются, общий .v8i затем редактируется напрямую.",
            "Общие списки баз задаются строками CommonInfoBases= в настройках 1CEStart (1CEStart.cfg), как и для самой 1С. Их базы " +
                "показываются после баз открытого списка с отметкой рамкой; база с тем же ID, что уже есть в дереве, не повторяется. " +
                "Такие базы можно запускать и смотреть их свойства, а изменить — только в самом списке.",
            "Сценарии резервирования: сценарий базы выбирается в её свойствах на странице «Резервное копирование». Каталог выгрузки: " +
                "«Каталог:» из свойств базы (относительный отсчитывается от «Каталога по-умолчанию»), «1. Каталог:» сценария, папка " +
                "Backup рядом с файловой базой, затем папка базы внутри «Каталога по-умолчанию». Если он не задан, для серверной базы " +
                "без своего каталога выгрузка попросит указать его.",
            "Шрифты и тема: строка списка увеличивается сама, если выбранному шрифту нужно больше места. Шрифт диалогов по умолчанию — 12; " +
                "сохранённый размер не меняется.",
            "Автообновление: проверка выполняется при запуске программы в фоне, загруженная версия начинает работать после перезапуска. " +
                "Прежние версии сохраняются в папке .versions рядом с программой; лишние, самые старые, удаляются при следующей установке " +
                "или восстановлении, копия установленной версии в это число не входит. Выпуски берутся по HTTPS через системный прокси или " +
                "HTTPS_PROXY; для закрытого репозитория нужен GitHub CLI (gh) с выполненным «gh auth login», токен хранит gh.",
        ]),
        // The explanations moved from the pages of the base and group dialogs into their tooltips (step 3a, 12.11.2026).
        new(PropertiesNotesTitle,
        [
            "Сведения: внешняя обработка запускается только в 1С:Предприятии. Для веб-базы дополнительные параметры и обработка " +
                "используют тонкий клиент; имя и пароль задаются на странице «Аутентификация».",
            "Платформа базы: пустой ручной путь использует общую настройку «Платформа 1С»; индивидуальное правило базы имеет приоритет. " +
                "Подбор версии: 8.3 или 8.3.27 — новейшая подходящая версия, полный номер — точное совпадение; при отсутствии совпадения " +
                "запуск отменяется. Версию в самом списке .v8i меняет «Назначить версию» (Shift+Ctrl+F8).",
            "Аутентификация: хранилище конфигурации подключается при запуске конфигуратора.",
            "Резервное копирование: пустой каталог — «1. Каталог» сценария, папка Backup рядом с файловой базой или общий «Каталог " +
                "по-умолчанию» из настроек; относительный каталог отсчитывается от общего. Префикс подставляется в шаблон имени файла " +
                "вместо {Prefix}. Сценарии задаются в настройках программы («Сценарии резервирования»).",
            "Группы: для вложенной группы указывается путь, например Проекты/Клиент. Каталог группы открывается по Shift+F12 и " +
                "предлагается для новых баз группы. В «Выборе группы» группу выбирают в дереве или указывают путь; пустое поле — вне групп.",
        ]),
    ];

    /// <summary>The title of the help section of the base and group dialogs; F1 in the base properties scrolls to it.</summary>
    public const string PropertiesNotesTitle = "Свойства базы и группы";

    /// <summary>The help as plain text: a section title, then «• keys = action» lines, a blank line between sections.</summary>
    public static string Text => string.Join(Environment.NewLine + Environment.NewLine,
        Sections.Select(s => string.Join(Environment.NewLine, s.Lines.Select(l => "• " + l).Prepend(s.Title))));
}
