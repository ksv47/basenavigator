namespace StartManager.Core;

[Flags]
public enum ShortcutModifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4 }

/// <summary>A key with its modifiers; Key is the name of the key in Avalonia's Key enumeration (F3, Insert, Enter, D…).</summary>
public sealed record Shortcut(string Key, ShortcutModifiers Modifiers = ShortcutModifiers.None)
{
    /// <summary>As the original shows a key everywhere (VCL ShortCutToText): Shift+Ctrl+Alt+, «Ins», «Del» — Shift+Ctrl+F8,
    /// Alt+Enter, Ins. The help, the menus and the toolbar hints all use this one text (Grok audit 09.11, P3).</summary>
    public override string ToString() =>
        (Modifiers.HasFlag(ShortcutModifiers.Shift) ? "Shift+" : "") + (Modifiers.HasFlag(ShortcutModifiers.Ctrl) ? "Ctrl+" : "") +
        (Modifiers.HasFlag(ShortcutModifiers.Alt) ? "Alt+" : "") + Key switch { "Insert" => "Ins", "Delete" => "Del", var name => name };
}

/// <summary>The keys of the main window. Window keys are handled before any control (the search field too); list keys only
/// while the list of bases has the focus, as the original's tree.</summary>
public enum ShortcutScope { Window, List }

public enum ShortcutCommand
{
    ShortHelp, Search, SearchByName, SearchByConnection, GoToFavorites, Snapshots, PlatformFilter, ServiceMenu,
    Launch, LaunchWithParameters, LaunchWithLogin, Designer, DesignerWithParameters, DesignerWithLogin,
    Rename, MoveToGroup, AddBase, DuplicateBase, AddGroup, Delete, Properties, BaseInfo, Reload, ToggleFavorite,
    AssignVersion, SetLock, RemoveLock, Backup, RestoreBackup, OpenFolder, ChangeFolder, CollapseAll, ExpandAll
}

public sealed record ShortcutBinding(ShortcutCommand Command, ShortcutScope Scope, Shortcut Shortcut);

/// <summary>The one table of the main window's keys: the window dispatches by it and «Краткая справка» lists it,
/// so the help cannot name a key the program does not handle.</summary>
public static class Shortcuts
{
    private const ShortcutModifiers Ctrl = ShortcutModifiers.Ctrl, Shift = ShortcutModifiers.Shift, Alt = ShortcutModifiers.Alt;

    public static IReadOnlyList<ShortcutBinding> All { get; } =
    [
        Window(ShortcutCommand.ShortHelp, "F1"), Window(ShortcutCommand.ShortHelp, "F1", Alt),
        Window(ShortcutCommand.Search, "F", Ctrl),
        Window(ShortcutCommand.SearchByName, "F", Ctrl | Shift),
        Window(ShortcutCommand.SearchByConnection, "F", Ctrl | Alt),
        Window(ShortcutCommand.GoToFavorites, "J", Ctrl),
        Window(ShortcutCommand.Snapshots, "L", Ctrl),
        Window(ShortcutCommand.PlatformFilter, "F8", Alt),
        // The original's «Сервисное меню» (actV8SystemMenu): the arrow before the list heading.
        Window(ShortcutCommand.ServiceMenu, "F10", Alt),
        List(ShortcutCommand.Launch, "F3"),
        Window(ShortcutCommand.LaunchWithParameters, "F3", Ctrl),
        List(ShortcutCommand.LaunchWithLogin, "F3", Shift),
        List(ShortcutCommand.Designer, "F4"),
        Window(ShortcutCommand.DesignerWithParameters, "F4", Ctrl),
        List(ShortcutCommand.DesignerWithLogin, "F4", Shift),
        List(ShortcutCommand.Rename, "F2"),
        List(ShortcutCommand.MoveToGroup, "M", Ctrl | Shift),
        List(ShortcutCommand.AddBase, "Insert"),
        List(ShortcutCommand.DuplicateBase, "Insert", Shift),
        List(ShortcutCommand.AddGroup, "Insert", Ctrl),
        List(ShortcutCommand.Delete, "Delete"),
        Window(ShortcutCommand.Properties, "Enter", Alt),
        Window(ShortcutCommand.BaseInfo, "I", Ctrl | Alt),
        Window(ShortcutCommand.Reload, "F5"),
        List(ShortcutCommand.ToggleFavorite, "F8"), Window(ShortcutCommand.ToggleFavorite, "D", Ctrl),
        Window(ShortcutCommand.AssignVersion, "F8", Ctrl | Shift),
        Window(ShortcutCommand.SetLock, "L", Ctrl | Alt),
        Window(ShortcutCommand.RemoveLock, "U", Ctrl | Alt),
        Window(ShortcutCommand.Backup, "F5", Ctrl | Shift),
        Window(ShortcutCommand.RestoreBackup, "F7", Ctrl | Shift),
        Window(ShortcutCommand.OpenFolder, "F12", Shift),
        Window(ShortcutCommand.ChangeFolder, "F11", Shift),
        Window(ShortcutCommand.CollapseAll, "F11"),
        Window(ShortcutCommand.ExpandAll, "F12"),
    ];

    private static ShortcutBinding Window(ShortcutCommand command, string key, ShortcutModifiers modifiers = ShortcutModifiers.None) =>
        new(command, ShortcutScope.Window, new(key, modifiers));
    private static ShortcutBinding List(ShortcutCommand command, string key, ShortcutModifiers modifiers = ShortcutModifiers.None) =>
        new(command, ShortcutScope.List, new(key, modifiers));

    public static IReadOnlyList<Shortcut> KeysOf(ShortcutCommand command) => All.Where(x => x.Command == command).Select(x => x.Shortcut).ToList();

    /// <summary>The command of a key in the scope, or null.</summary>
    public static ShortcutCommand? Find(ShortcutScope scope, Shortcut shortcut) =>
        All.FirstOrDefault(x => x.Scope == scope && x.Shortcut == shortcut)?.Command;
}
