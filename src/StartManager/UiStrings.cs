using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace StartManager;

// Russian texts for the parts Fluent draws itself: the edit menu of text fields and the scroll bars.
internal static class UiStrings
{
    private static readonly (string Key, string Text)[] Resources =
    [
        ("StringTextFlyoutCutText", "Вырезать"), ("StringTextFlyoutCopyText", "Копировать"), ("StringTextFlyoutPasteText", "Вставить"),
        ("StringScrollBarScrollHere", "Прокрутить сюда"), ("StringScrollBarTop", "В начало"), ("StringScrollBarBottom", "В конец"),
        ("StringScrollBarPageUp", "На страницу вверх"), ("StringScrollBarPageDown", "На страницу вниз"),
        ("StringScrollBarScrollUp", "Прокрутить вверх"), ("StringScrollBarScrollDown", "Прокрутить вниз"),
        ("StringScrollBarLeftEdge", "К левому краю"), ("StringScrollBarRightEdge", "К правому краю"),
        ("StringScrollBarPageLeft", "На страницу влево"), ("StringScrollBarPageRight", "На страницу вправо"),
        ("StringScrollBarScrollLeft", "Прокрутить влево"), ("StringScrollBarScrollRight", "Прокрутить вправо"),
    ];

    // Fluent names the scroll bar buttons in English in its template ("Line up", "Column left"); a screen reader reads them aloud.
    private static readonly (string Orientation, string Part, string Name)[] ScrollButtons =
    [
        (":vertical", "PART_LineUpButton", "Прокрутить вверх"), (":vertical", "PART_PageUpButton", "На страницу вверх"),
        (":vertical", "PART_PageDownButton", "На страницу вниз"), (":vertical", "PART_LineDownButton", "Прокрутить вниз"),
        (":horizontal", "PART_LineUpButton", "Прокрутить влево"), (":horizontal", "PART_PageUpButton", "На страницу влево"),
        (":horizontal", "PART_PageDownButton", "На страницу вправо"), (":horizontal", "PART_LineDownButton", "Прокрутить вправо"),
    ];

    public static void Initialize(Application app)
    {
        foreach (var (key, text) in Resources) app.Resources[key] = text;
        // The template sets the names with Template priority; a selector with a pseudo-class is a style trigger and wins over it.
        foreach (var (orientation, part, name) in ScrollButtons)
            app.Styles.Add(new Style(s => s.OfType<ScrollBar>().Class(orientation).Template().OfType<RepeatButton>().Name(part))
            { Setters = { new Setter(AutomationProperties.NameProperty, name) } });
        foreach (var orientation in new[] { ":vertical", ":horizontal" })
            app.Styles.Add(new Style(s => s.OfType<ScrollBar>().Class(orientation).Template().OfType<Thumb>())
            { Setters = { new Setter(AutomationProperties.NameProperty, "Положение") } });
    }
}
