using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class MainCommandsTests
{
    private static CommandState State(SelectionKind selection, bool own = true, ConnectionKind? kind = ConnectionKind.File,
        bool loaded = true, bool reading = false, bool readOnly = false, bool groupFolder = false) =>
        new(loaded, reading, readOnly, selection, own, selection is SelectionKind.Base or SelectionKind.FavoriteShortcut ? kind : null, groupFolder);

    private static readonly MainCommand[] AlwaysOn =
        [MainCommand.ShortHelp, MainCommand.Search, MainCommand.SearchByName, MainCommand.SearchByConnection, MainCommand.GoToFavorites,
         MainCommand.PlatformFilter, MainCommand.ServiceMenu, MainCommand.Reload, MainCommand.CollapseAll, MainCommand.ExpandAll];

    private static HashSet<MainCommand> Available(CommandState state) =>
        Enum.GetValues<MainCommand>().Where(x => MainCommands.CanExecute(x, state)).ToHashSet();

    public static TheoryData<string, CommandState, MainCommand[]> States => new()
    {
        // A file base of the open list: everything but the folder of a group.
        { "own file base", State(SelectionKind.Base), [.. AlwaysOn, MainCommand.Snapshots, MainCommand.Launch, MainCommand.LaunchWithParameters,
            MainCommand.LaunchWithLogin, MainCommand.Designer, MainCommand.DesignerWithParameters, MainCommand.DesignerWithLogin, MainCommand.Rename,
            MainCommand.MoveToGroup, MainCommand.AddBase, MainCommand.DuplicateBase, MainCommand.AddGroup, MainCommand.Delete, MainCommand.Properties,
            MainCommand.BaseInfo, MainCommand.ToggleFavorite, MainCommand.AssignVersion, MainCommand.SetLock, MainCommand.RemoveLock, MainCommand.Backup,
            MainCommand.RestoreBackup, MainCommand.OpenFolder, MainCommand.ChangeFolder, MainCommand.ClearCache, MainCommand.PropertiesMenu,
            MainCommand.SnapshotsMenu] },
        // The same base on a read-only list: only what does not write the list.
        { "own file base, read-only list", State(SelectionKind.Base, readOnly: true), [.. AlwaysOn, MainCommand.Snapshots, MainCommand.Launch,
            MainCommand.LaunchWithParameters, MainCommand.LaunchWithLogin, MainCommand.Designer, MainCommand.DesignerWithParameters,
            MainCommand.DesignerWithLogin, MainCommand.Properties, MainCommand.BaseInfo, MainCommand.ToggleFavorite, MainCommand.SetLock,
            MainCommand.RemoveLock, MainCommand.Backup, MainCommand.RestoreBackup, MainCommand.OpenFolder, MainCommand.ClearCache,
            MainCommand.PropertiesMenu, MainCommand.SnapshotsMenu] },
        // A server base of a common list: started and viewed; the lock is set through its properties, never removed here.
        { "common server base", State(SelectionKind.Base, own: false, kind: ConnectionKind.Server), [.. AlwaysOn, MainCommand.Snapshots,
            MainCommand.Launch, MainCommand.LaunchWithParameters, MainCommand.LaunchWithLogin, MainCommand.Designer, MainCommand.DesignerWithParameters,
            MainCommand.DesignerWithLogin, MainCommand.AddBase, MainCommand.AddGroup, MainCommand.Properties, MainCommand.BaseInfo,
            MainCommand.ToggleFavorite, MainCommand.Backup, MainCommand.RestoreBackup, MainCommand.ClearCache, MainCommand.PropertiesMenu,
            MainCommand.SnapshotsMenu] },
        // A file base of a common list: started, viewed, its folder opened and its lock set through the properties; nothing of
        // its record changes (Grok audit 09.11).
        { "common file base", State(SelectionKind.Base, own: false), [.. AlwaysOn, MainCommand.Snapshots, MainCommand.Launch,
            MainCommand.LaunchWithParameters, MainCommand.LaunchWithLogin, MainCommand.Designer, MainCommand.DesignerWithParameters,
            MainCommand.DesignerWithLogin, MainCommand.AddBase, MainCommand.AddGroup, MainCommand.Properties, MainCommand.BaseInfo,
            MainCommand.ToggleFavorite, MainCommand.SetLock, MainCommand.Backup, MainCommand.RestoreBackup, MainCommand.OpenFolder,
            MainCommand.ClearCache, MainCommand.PropertiesMenu, MainCommand.SnapshotsMenu] },
        // A favorite shortcut of an own base on a writable list: what its base can, its record is the list's.
        { "own favorite shortcut", State(SelectionKind.FavoriteShortcut), [.. AlwaysOn, MainCommand.Snapshots, MainCommand.Launch,
            MainCommand.LaunchWithParameters, MainCommand.LaunchWithLogin, MainCommand.Designer, MainCommand.DesignerWithParameters,
            MainCommand.DesignerWithLogin, MainCommand.Rename, MainCommand.MoveToGroup, MainCommand.AddBase, MainCommand.DuplicateBase,
            MainCommand.AddGroup, MainCommand.Delete, MainCommand.Properties, MainCommand.BaseInfo, MainCommand.ToggleFavorite,
            MainCommand.AssignVersion, MainCommand.SetLock, MainCommand.RemoveLock, MainCommand.Backup, MainCommand.RestoreBackup,
            MainCommand.OpenFolder, MainCommand.ChangeFolder, MainCommand.ClearCache, MainCommand.PropertiesMenu, MainCommand.SnapshotsMenu] },
        // A web base: no configurator, no folder, no backup, no lock.
        { "own web base", State(SelectionKind.Base, kind: ConnectionKind.Web), [.. AlwaysOn, MainCommand.Snapshots, MainCommand.Launch,
            MainCommand.LaunchWithParameters, MainCommand.LaunchWithLogin, MainCommand.Rename, MainCommand.MoveToGroup, MainCommand.AddBase,
            MainCommand.AddGroup, MainCommand.Delete, MainCommand.Properties, MainCommand.BaseInfo, MainCommand.ToggleFavorite,
            MainCommand.AssignVersion, MainCommand.ClearCache, MainCommand.PropertiesMenu, MainCommand.SnapshotsMenu] },
        // A favorite shortcut on a read-only list: it still leaves «Избранное» (the profile, not the .v8i).
        { "favorite shortcut, read-only list", State(SelectionKind.FavoriteShortcut, readOnly: true), [.. AlwaysOn, MainCommand.Snapshots,
            MainCommand.Launch, MainCommand.LaunchWithParameters, MainCommand.LaunchWithLogin, MainCommand.Designer, MainCommand.DesignerWithParameters,
            MainCommand.DesignerWithLogin, MainCommand.Delete, MainCommand.Properties, MainCommand.BaseInfo, MainCommand.ToggleFavorite,
            MainCommand.SetLock, MainCommand.RemoveLock, MainCommand.Backup, MainCommand.RestoreBackup, MainCommand.OpenFolder,
            MainCommand.ClearCache, MainCommand.PropertiesMenu, MainCommand.SnapshotsMenu] },
        // A group with a folder: its properties, name, version and removal; the folder opens.
        { "own group with a folder", State(SelectionKind.Group, groupFolder: true), [.. AlwaysOn, MainCommand.Snapshots, MainCommand.Rename,
            MainCommand.AddBase, MainCommand.AddGroup, MainCommand.Delete, MainCommand.Properties, MainCommand.AssignVersion, MainCommand.OpenFolder,
            MainCommand.ClearCache, MainCommand.PropertiesMenu, MainCommand.SnapshotsMenu] },
        // On a read-only list the group's dialog stays (its folder is a local setting); nothing else of the group changes.
        { "own group, read-only list", State(SelectionKind.Group, readOnly: true), [.. AlwaysOn, MainCommand.Snapshots, MainCommand.Rename,
            MainCommand.Properties, MainCommand.ClearCache, MainCommand.PropertiesMenu, MainCommand.SnapshotsMenu] },
        // A group only bases of common lists are in: nothing of it changes here.
        { "group of common lists", State(SelectionKind.Group, own: false), [.. AlwaysOn, MainCommand.Snapshots, MainCommand.AddBase,
            MainCommand.AddGroup, MainCommand.ClearCache, MainCommand.PropertiesMenu, MainCommand.SnapshotsMenu] },
        // The cache of a missing configuration is removed on a read-only list too.
        { "orphan, read-only list", State(SelectionKind.Orphan, readOnly: true), [.. AlwaysOn, MainCommand.Snapshots, MainCommand.Delete,
            MainCommand.ClearCache, MainCommand.PropertiesMenu, MainCommand.SnapshotsMenu] },
        { "orphan section", State(SelectionKind.Orphans), [.. AlwaysOn, MainCommand.Snapshots, MainCommand.AddBase, MainCommand.AddGroup,
            MainCommand.Delete, MainCommand.ClearCache, MainCommand.PropertiesMenu, MainCommand.SnapshotsMenu] },
        // A section row or no row: what does not need a selection.
        { "section row", State(SelectionKind.None), [.. AlwaysOn, MainCommand.Snapshots, MainCommand.AddBase, MainCommand.AddGroup,
            MainCommand.ClearCache, MainCommand.PropertiesMenu, MainCommand.SnapshotsMenu] },
        // While the list is read, or after it failed to load: the search, the help, «Сервис», rereading and the view only.
        { "list being read", State(SelectionKind.Base, reading: true), [.. AlwaysOn, MainCommand.OpenFolder] },
        { "list not loaded", State(SelectionKind.Group, loaded: false, groupFolder: true), [.. AlwaysOn, MainCommand.OpenFolder] },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Every_command_follows_its_rule(string name, CommandState state, MainCommand[] expected)
    {
        var available = Available(state);
        Assert.True(available.SetEquals(expected), $"{name}: extra [{string.Join(", ", available.Except(expected))}], " +
            $"missing [{string.Join(", ", expected.Except(available))}]");
    }

    [Fact]
    public void The_table_has_every_command_once_and_every_key_command()
    {
        Assert.Equal(Enum.GetValues<MainCommand>().OrderBy(x => x), MainCommands.All.Select(x => x.Command).OrderBy(x => x));
        var keyed = MainCommands.All.Where(x => x.Shortcut is not null).Select(x => x.Shortcut!.Value).ToList();
        Assert.Equal(keyed.Count, keyed.Distinct().Count());
        Assert.Equal(Enum.GetValues<ShortcutCommand>().OrderBy(x => x), keyed.OrderBy(x => x));
        Assert.All(Enum.GetValues<ShortcutCommand>(), c => Assert.Equal(c, MainCommands.Info(MainCommands.Of(c)).Shortcut));
        Assert.All(MainCommands.All, x => { Assert.NotEmpty(x.Caption); Assert.NotEmpty(x.Hint); });
    }

    [Fact]
    public void The_key_of_a_command_is_its_first_key_of_the_shortcut_table()
    {
        foreach (var info in MainCommands.All)
        {
            var key = MainCommands.Key(info.Command);
            if (info.Shortcut is { } shortcut) Assert.Equal(Shortcuts.KeysOf(shortcut)[0], key);
            else { Assert.Null(key); Assert.Null(MainCommands.KeyText(info.Command)); Assert.Equal(info.Hint, MainCommands.Tip(info.Command)); }
        }
        // F8 before Ctrl+D, F1 before Alt+F1: the key the original's menus show.
        Assert.Equal(new Shortcut("F8"), MainCommands.Key(MainCommand.ToggleFavorite));
        Assert.Equal(new Shortcut("F1"), MainCommands.Key(MainCommand.ShortHelp));
    }

    [Theory]
    // As the original's menus: Shift before Ctrl before Alt; «Ins» and «Del».
    [InlineData(MainCommand.AddBase, "Ins")]
    [InlineData(MainCommand.AddGroup, "Ctrl+Ins")]
    [InlineData(MainCommand.DuplicateBase, "Shift+Ins")]
    [InlineData(MainCommand.Delete, "Del")]
    [InlineData(MainCommand.AssignVersion, "Shift+Ctrl+F8")]
    [InlineData(MainCommand.MoveToGroup, "Shift+Ctrl+M")]
    [InlineData(MainCommand.BaseInfo, "Ctrl+Alt+I")]
    [InlineData(MainCommand.Properties, "Alt+Enter")]
    [InlineData(MainCommand.ServiceMenu, "Alt+F10")]
    [InlineData(MainCommand.OpenFolder, "Shift+F12")]
    [InlineData(MainCommand.SearchByName, "Shift+Ctrl+F")]
    public void Keys_are_shown_as_the_original_menus_show_them(MainCommand command, string text) =>
        Assert.Equal(text, MainCommands.KeyText(command));

    [Theory]
    // The toolbar hints of the original's smMainForm.dfm, with the key.
    [InlineData(MainCommand.AddBase, "Зарегистрировать информационную базу · Ins")]
    [InlineData(MainCommand.AddGroup, "Создать новую группу · Ctrl+Ins")]
    [InlineData(MainCommand.Delete, "Удалить из списка · Del")]
    [InlineData(MainCommand.Properties, "Свойства инф. базы · Alt+Enter")]
    [InlineData(MainCommand.ClearCache, "Очистка временных файлов")]
    [InlineData(MainCommand.Snapshots, "Сохраненные снимки инф. баз · Ctrl+L")]
    [InlineData(MainCommand.Reload, "Перечитать список баз · F5")]
    [InlineData(MainCommand.ExpandAll, "Развернуть все · F12")]
    [InlineData(MainCommand.CollapseAll, "Свернуть все · F11")]
    [InlineData(MainCommand.ServiceMenu, "Сервисное меню · Alt+F10")]
    [InlineData(MainCommand.PlatformFilter, "Отбор по версии платформы · Alt+F8")]
    public void Toolbar_hints_are_the_original_ones(MainCommand command, string tip) => Assert.Equal(tip, MainCommands.Tip(command));

    public static TheoryData<MainCommand, CommandState, CommandRefusal> Refusals => new()
    {
        // The read-only list first, as ListLocked comes before CommonLocked in the actions.
        { MainCommand.Rename, State(SelectionKind.Base, readOnly: true), CommandRefusal.ReadOnlyList },
        { MainCommand.Rename, State(SelectionKind.Base, own: false, readOnly: true), CommandRefusal.ReadOnlyList },
        { MainCommand.Rename, State(SelectionKind.Base, own: false), CommandRefusal.CommonBase },
        { MainCommand.Rename, State(SelectionKind.Group, own: false), CommandRefusal.CommonGroup },
        { MainCommand.Rename, State(SelectionKind.None), CommandRefusal.None },
        { MainCommand.Delete, State(SelectionKind.Base, readOnly: true), CommandRefusal.ReadOnlyList },
        { MainCommand.Delete, State(SelectionKind.Group, readOnly: true), CommandRefusal.ReadOnlyList },
        { MainCommand.Delete, State(SelectionKind.Group, own: false), CommandRefusal.CommonGroup },
        { MainCommand.Delete, State(SelectionKind.Base, own: false), CommandRefusal.CommonBase },
        { MainCommand.Delete, State(SelectionKind.None), CommandRefusal.None },
        { MainCommand.AddBase, State(SelectionKind.None, readOnly: true), CommandRefusal.ReadOnlyList },
        { MainCommand.AddGroup, State(SelectionKind.Base, readOnly: true), CommandRefusal.ReadOnlyList },
        { MainCommand.MoveToGroup, State(SelectionKind.FavoriteShortcut, readOnly: true), CommandRefusal.ReadOnlyList },
        { MainCommand.MoveToGroup, State(SelectionKind.Group), CommandRefusal.None },
        { MainCommand.DuplicateBase, State(SelectionKind.Base, readOnly: true), CommandRefusal.ReadOnlyList },
        { MainCommand.DuplicateBase, State(SelectionKind.Base, kind: ConnectionKind.Server), CommandRefusal.None },
        { MainCommand.AssignVersion, State(SelectionKind.Group, own: false), CommandRefusal.CommonGroup },
        { MainCommand.AssignVersion, State(SelectionKind.Base, readOnly: true), CommandRefusal.ReadOnlyList },
        { MainCommand.ChangeFolder, State(SelectionKind.Base, kind: ConnectionKind.Server), CommandRefusal.FileFolderChange },
        { MainCommand.ChangeFolder, State(SelectionKind.Base, readOnly: true), CommandRefusal.ReadOnlyList },
        { MainCommand.ChangeFolder, State(SelectionKind.Base, readOnly: true, reading: true), CommandRefusal.None },
        { MainCommand.OpenFolder, State(SelectionKind.Base, kind: ConnectionKind.Web), CommandRefusal.FileFolder },
        { MainCommand.OpenFolder, State(SelectionKind.Group, reading: true), CommandRefusal.GroupFolder },
        { MainCommand.OpenFolder, State(SelectionKind.Orphan), CommandRefusal.None },
        { MainCommand.Properties, State(SelectionKind.Group, own: false), CommandRefusal.CommonGroup },
        { MainCommand.Properties, State(SelectionKind.None), CommandRefusal.None },
        // F4 and Ctrl+F4 on a web base; Ctrl+F3 without a base.
        { MainCommand.Designer, State(SelectionKind.Base, kind: ConnectionKind.Web), CommandRefusal.WebDesigner },
        { MainCommand.DesignerWithParameters, State(SelectionKind.Base, kind: ConnectionKind.Web), CommandRefusal.WebDesigner },
        { MainCommand.LaunchWithParameters, State(SelectionKind.None), CommandRefusal.SelectBase },
        { MainCommand.ToggleFavorite, State(SelectionKind.Group), CommandRefusal.SelectBase },
        { MainCommand.BaseInfo, State(SelectionKind.Orphan), CommandRefusal.ChooseBase },
        { MainCommand.RemoveLock, State(SelectionKind.Base, kind: ConnectionKind.Server), CommandRefusal.FileLock },
        // Ctrl+Alt+L: the text of the properties' page «Блокировка» for such a base.
        { MainCommand.SetLock, State(SelectionKind.Base, kind: ConnectionKind.Server), CommandRefusal.SetLockKind },
        { MainCommand.SetLock, State(SelectionKind.FavoriteShortcut, kind: ConnectionKind.Web), CommandRefusal.SetLockKind },
        { MainCommand.SetLock, State(SelectionKind.Group), CommandRefusal.None },
        { MainCommand.RemoveLock, State(SelectionKind.Base, own: false), CommandRefusal.CommonBase },
        { MainCommand.Backup, State(SelectionKind.Base, reading: true), CommandRefusal.Busy },
        { MainCommand.RestoreBackup, State(SelectionKind.None), CommandRefusal.SelectBase },
        { MainCommand.Backup, State(SelectionKind.Base, kind: ConnectionKind.Web), CommandRefusal.BackupKind },
        // Silent as before: the list is being read, or the action said nothing.
        { MainCommand.Rename, State(SelectionKind.Base, readOnly: true, reading: true), CommandRefusal.None },
        { MainCommand.LaunchWithParameters, State(SelectionKind.None, loaded: false), CommandRefusal.None },
        { MainCommand.SetLock, State(SelectionKind.Base, readOnly: true, reading: true), CommandRefusal.None },
        { MainCommand.Snapshots, State(SelectionKind.Base, reading: true), CommandRefusal.None },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void An_unavailable_command_gives_the_reason_its_action_gave(MainCommand command, CommandState state, CommandRefusal refusal)
    {
        Assert.False(MainCommands.CanExecute(command, state));
        Assert.Equal(refusal, MainCommands.Refusal(command, state));
    }

    [Fact]
    public void An_available_command_has_no_refusal_and_every_fixed_refusal_has_its_text()
    {
        foreach (var row in States)
            foreach (var command in Enum.GetValues<MainCommand>())
                if (MainCommands.CanExecute(command, (CommandState)row[1])) Assert.Equal(CommandRefusal.None, MainCommands.Refusal(command, (CommandState)row[1]));
        foreach (var refusal in Enum.GetValues<CommandRefusal>())
        {
            var text = MainCommands.RefusalText(refusal);
            // The window words these with the record: the read-only reason, the common list, the group.
            if (refusal is CommandRefusal.None or CommandRefusal.ReadOnlyList or CommandRefusal.CommonBase or CommandRefusal.CommonGroup or CommandRefusal.GroupFolder)
                Assert.Null(text);
            else Assert.False(string.IsNullOrWhiteSpace(text));
        }
        Assert.Equal("Сначала выберите базу в списке.", MainCommands.RefusalText(CommandRefusal.SelectBase));
        Assert.Equal("Блокировка доступна только для файлового варианта информационной базы.", MainCommands.RefusalText(CommandRefusal.SetLockKind));
    }

    [Theory]
    [InlineData(SelectionKind.Orphan)]
    [InlineData(SelectionKind.Orphans)]
    public void Removing_the_cache_turns_its_removal_off_until_it_ends(SelectionKind selection)
    {
        // Available → under way (the question, the removal) → ended, cancelled or failed: the same state again.
        var idle = State(selection, readOnly: true);
        var busy = idle with { ClearingOrphans = true };
        Assert.True(MainCommands.CanExecute(MainCommand.Delete, idle));
        Assert.False(MainCommands.CanExecute(MainCommand.Delete, busy));
        Assert.Equal(CommandRefusal.None, MainCommands.Refusal(MainCommand.Delete, busy));
        Assert.True(MainCommands.CanExecute(MainCommand.Delete, busy with { ClearingOrphans = false }));
        // Only the removal of the cache waits: the rest of the window, and Del of other rows, stay as they were.
        Assert.True(Available(busy).SetEquals(Available(idle).Where(x => x != MainCommand.Delete)));
        foreach (var other in new[] { SelectionKind.Base, SelectionKind.Group, SelectionKind.FavoriteShortcut })
            Assert.True(Available(State(other)).SetEquals(Available(State(other) with { ClearingOrphans = true })));
    }

    [Fact]
    public void Menus_hints_and_the_help_show_a_key_by_one_text()
    {
        foreach (var info in MainCommands.All.Where(x => x.Shortcut is not null))
            Assert.Equal(Shortcuts.KeysOf(info.Shortcut!.Value)[0].ToString(), MainCommands.KeyText(info.Command));
        // «Краткая справка» lists the same text.
        Assert.Contains("• Shift+Ctrl+F = Поиск по наименованию", ShortHelp.Text);
        Assert.DoesNotContain("Ctrl+Shift+", ShortHelp.Text);
    }

    [Fact]
    public void Delete_on_a_favorite_shortcut_says_it_removes_it_from_the_favorites()
    {
        var shortcut = State(SelectionKind.FavoriteShortcut, readOnly: true);
        Assert.Equal("Удалить из избранных", MainCommands.Caption(MainCommand.Delete, shortcut));
        Assert.Equal("Удалить из избранных · Del", MainCommands.Tip(MainCommand.Delete, shortcut));
        Assert.Equal("Удалить из списка…", MainCommands.Caption(MainCommand.Delete, State(SelectionKind.Base)));
        Assert.Equal("Удалить из списка · Del", MainCommands.Tip(MainCommand.Delete, State(SelectionKind.Base)));
        Assert.All(Enum.GetValues<MainCommand>().Where(x => x != MainCommand.Delete),
            x => Assert.Equal(MainCommands.Tip(x), MainCommands.Tip(x, shortcut)));
    }

    [Fact]
    public void Alt_F10_opens_the_service_menu_from_the_whole_window() =>
        Assert.Equal(ShortcutCommand.ServiceMenu, Shortcuts.Find(ShortcutScope.Window, new Shortcut("F10", ShortcutModifiers.Alt)));
}
