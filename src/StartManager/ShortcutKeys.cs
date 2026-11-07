using Avalonia.Input;
using StartManager.Core;

namespace StartManager;

/// <summary>Avalonia's keys for the table of Shortcuts. Key names are parsed (Key.Enter and Key.Return are one value, so a
/// key is never compared by its name); the modifiers must match exactly, a Windows/Command key makes no match.</summary>
internal static class ShortcutKeys
{
    private static readonly Dictionary<(ShortcutScope, Key, KeyModifiers), ShortcutCommand> Map = Shortcuts.All
        .ToDictionary(x => (x.Scope, Enum.Parse<Key>(x.Shortcut.Key), Modifiers(x.Shortcut.Modifiers)), x => x.Command);

    private static KeyModifiers Modifiers(ShortcutModifiers modifiers) =>
        (modifiers.HasFlag(ShortcutModifiers.Ctrl) ? KeyModifiers.Control : KeyModifiers.None) |
        (modifiers.HasFlag(ShortcutModifiers.Shift) ? KeyModifiers.Shift : KeyModifiers.None) |
        (modifiers.HasFlag(ShortcutModifiers.Alt) ? KeyModifiers.Alt : KeyModifiers.None);

    public static ShortcutCommand? Find(ShortcutScope scope, KeyEventArgs e) =>
        Map.TryGetValue((scope, e.Key, e.KeyModifiers), out var command) ? command : null;
}
