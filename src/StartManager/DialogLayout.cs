using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace StartManager;

internal static class DialogLayout
{
    public static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    public static Control Field(string caption, Control field) => Field(Label(caption), field);

    /// <summary>The wrapped texts standing in a vertical panel of <paramref name="root"/> (hints under the fields) never get wider than
    /// that panel. When the vertical scroll bar of a page appears after the page was measured without it, the panel is arranged
    /// narrower than the texts were wrapped for and their end went under the bar (the /UC warning at 22 pt, Linux check 27.10).
    /// The arranged width of the panel bounds them, so they wrap again to the column.</summary>
    public static void WrapWithin(Control root)
    {
        foreach (var stack in Avalonia.LogicalTree.LogicalExtensions.GetSelfAndLogicalDescendants(root).OfType<StackPanel>())
        {
            if (stack.Orientation != Orientation.Vertical) continue;
            stack.SizeChanged += (_, e) =>
            {
                if (!e.WidthChanged) return;
                foreach (var text in stack.Children.OfType<TextBlock>().Where(x => x.TextWrapping != TextWrapping.NoWrap))
                    text.MaxWidth = Math.Max(0, stack.Bounds.Width - text.Margin.Left - text.Margin.Right);
            };
        }
    }

    /// <summary>«…» inside a field (TextBox.InnerRightContent), as the buttoned edits of the original: a flat button that opens
    /// a choice for the field. <paramref name="name"/> is its tooltip and accessible name.</summary>
    public static Button Ellipsis(string name, Func<Task> action)
    {
        var button = new Button { Content = new TextBlock { Text = "…", FontWeight = FontWeight.Bold }, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(5, 1), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(button, name); ToolTip.SetTip(button, name);
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>A field whose caption is itself a control, e.g. the check box that enables it.</summary>
    public static Control Field(Control label, Control field)
    {
        label.VerticalAlignment = VerticalAlignment.Center;
        field.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = new("150,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 10, Classes = { "field" } };
        row.Children.Add(label); row.Children.Add(field); Grid.SetColumn(field, 1);
        CaptionScope? joined = null;
        void Layout()
        {
            var width = row.Bounds.Width; var caption = row.GetValue(CaptionWidthProperty);
            bool stacked; double column;
            if (double.IsNaN(caption)) { stacked = width < 440 || row.FontSizeForLayout() > 18; column = 150; }
            else
            {
                // A page that measured its captions (AlignCaptions): one column as wide as the longest of them, so every caption
                // stays on one line and the fields of the page start on one vertical; the caption goes above its field only when
                // the field would be left too narrow.
                column = caption;
                // A list of fixed choices (PreferredWidth) stacks before its longest choice is cut (Grok audit 11.11). A short field
                // (a port, a password beside another field) names its own least width (LeastWidth).
                var preferred = field.GetValue(PreferredWidthProperty);
                var least = field.GetValue(LeastWidthProperty) is var own && !double.IsNaN(own) ? own : Math.Max(160, row.FontSizeForLayout() * 10);
                stacked = width - caption - 10 < Math.Max(least, double.IsNaN(preferred) ? 0 : preferred);
                // One rule for the page (З1 of the Linux check 12.11): when any shown field of it goes under its caption, all its
                // captions stand above their fields, so the page never mixes the two layouts in a ragged column.
                if (row.GetValue(ScopeProperty) is { } scope)
                {
                    if (joined != scope) { joined = scope; scope.Changed += () => { if (row.Bounds.Width > 0) Layout(); }; }
                    stacked = scope.Report(row, stacked) || stacked;
                }
            }
            row.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(column);
            row.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            // Beside its field a caption may stand at the right edge of the column (RightCaptions); above it, always at the left.
            label.HorizontalAlignment = !stacked && row.GetValue(RightCaptionsProperty) ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            Grid.SetColumn(field, stacked ? 0 : 1); Grid.SetRow(field, stacked ? 1 : 0);
            // The empty second column would still keep the spacing and make a stacked field narrower than its neighbours.
            row.RowSpacing = stacked ? 3 : 0; row.ColumnSpacing = stacked ? 0 : 10;
        }
        row.SizeChanged += (_, _) => Layout();
        row.PropertyChanged += (_, e) => { if (e.Property == CaptionWidthProperty && row.Bounds.Width > 0) Layout(); };
        // A text caption follows the state of its own field: grey (DisabledText) while the field is disabled. The style of disabled texts
        // did not reach the captions of the two-column rows (Codex recheck 3 and Linux check 3 of 12.11, Н1а), so it is set here for every row.
        // A caption with its own colour (the bold grey branch caption of the settings) keeps it.
        if (label is TextBlock caption && !caption.IsSet(TextBlock.ForegroundProperty))
        {
            void Shade()
            {
                if (field.IsEffectivelyEnabled) caption.ClearValue(TextBlock.ForegroundProperty);
                else caption.Foreground = UiColors.DisabledText;
            }
            field.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Input.InputElement.IsEffectivelyEnabledProperty) Shade(); };
            Shade();
        }
        return row;
    }

    /// <summary>The width of the caption column of the <see cref="Field(Control, Control)"/> rows inside a control; not set (NaN)
    /// keeps the fixed column of 150 of the dialogs that do not measure their captions.</summary>
    public static readonly AttachedProperty<double> CaptionWidthProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("CaptionWidth", typeof(DialogLayout), double.NaN, inherits: true);

    /// <summary>The captions of the <see cref="Field(Control, Control)"/> rows inside a control stand at the right edge of their
    /// column, next to the fields, as in the original's base properties (smFormProperty); off (the default): at the left edge.</summary>
    public static readonly AttachedProperty<bool> RightCaptionsProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("RightCaptions", typeof(DialogLayout), false, inherits: true);

    /// <summary>The width a field needs to show its value whole, e.g. a list of fixed choices as wide as its longest one; a field row
    /// puts such a field under its caption rather than cut it beside the caption.</summary>
    public static readonly AttachedProperty<double> PreferredWidthProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("PreferredWidth", typeof(DialogLayout), double.NaN);

    /// <summary>The least width a short field keeps beside its caption in a field row (a port, a password in two columns); not set
    /// (NaN): 160 or 10 × the font. Narrower than that, the field goes under its caption.</summary>
    public static readonly AttachedProperty<double> LeastWidthProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("LeastWidth", typeof(DialogLayout), double.NaN);

    /// <summary>A list of fixed choices that prefers the width of its longest choice (with its arrow) in a field row.</summary>
    public static ComboBox FitChoices(ComboBox box, UiTypography type)
    {
        var widest = (box.ItemsSource as System.Collections.IEnumerable)?.Cast<object>()
            .Select(x => UiTypography.Measure(x?.ToString() ?? "", type.DialogFamily, type.DialogSize).Width).DefaultIfEmpty(0).Max() ?? 0;
        box.SetValue(PreferredWidthProperty, Math.Ceiling(widest) + 48);
        return box;
    }

    /// <summary>The wheel over a closed choice never changes its value; it scrolls the page around it, as over any other field
    /// (Linux 4, Н1: scrolling a long page, the focused «Использование Proxy» passed under the pointer and silently switched its
    /// mode). Avalonia 12.1.2 changes the value in OnPointerWheelChanged, reached by the bubbling event: ComboBox when focused
    /// (SelectNext/SelectPrevious), the ButtonSpinner of NumericUpDown when its text is focused, CalendarDatePicker always (a day
    /// back or forth). Registered once for every window: the tunnelling event is marked handled at the control, so neither the
    /// control nor its parts see it, and a copy is raised from the control's parent, where the page's ScrollViewer (its
    /// ScrollContentPresenter) scrolls by its own rules or, with nothing to scroll, nothing happens. An open dropdown keeps the
    /// wheel: its items and the calendar lie in the popup, whose events route through the control (PopupRoot.InteractiveParent),
    /// and the default behaviour is kept for them. The keyboard is not touched.</summary>
    public static void WheelScrollsPage()
    {
        var wheel = Avalonia.Input.InputElement.PointerWheelChangedEvent;
        wheel.AddClassHandler<ComboBox>((box, e) => PassWheel(box, e, box.IsDropDownOpen), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        wheel.AddClassHandler<CalendarDatePicker>((picker, e) => PassWheel(picker, e, picker.IsDropDownOpen), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        wheel.AddClassHandler<NumericUpDown>((number, e) => PassWheel(number, e, false), Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Escape while a popup is open closes only the popup, never its window (Linux 5, Н1: Escape in the open calendar of
    /// «Начало» on «Блокировка» closed the whole properties dialog and lost its edits). Avalonia 12.1.2: CalendarDatePicker's
    /// Calendar_KeyDown closes the calendar on Escape but leaves the key unhandled; the key bubbles from the popup through the Popup
    /// (PopupRoot.InteractiveParent) up to the window, where Button.RootCancelKeyDown of the IsCancel button («Закрыть», «Отмена»)
    /// clicks it. ComboBox, menus and flyouts mark the key handled themselves. Registered once for every window, for an unhandled
    /// Escape: reaching a Popup, it came from that popup's content; reaching a window, focus is outside every popup. Either way
    /// the latest opened popup of that scope (the Popup and the popups opened inside it; the window and all its popups, nested
    /// ones through their hosts) closes and the key ends there; a popup that already closed itself (the calendar) only ends it.
    /// Class handlers run before the instance handlers of the same element, so the window's cancel button never sees the key.
    /// An Escape that started while a popup was open never reaches cancel, whoever closed the popup (a Button's own Flyout).
    /// Tooltips are passive and never take the key. Nothing open: Escape behaves as before.</summary>
    public static void EscapeClosesPopupFirst()
    {
        var key = Avalonia.Input.InputElement.KeyDownEvent;
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, _) =>
        {
            openPopups.Remove(popup);
            if (popup.IsOpen) openPopups.Add(popup);
        });
        key.AddClassHandler<Popup>((popup, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape) return;
            e.Handled = true;
            CloseLatest(popup);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        // A control may close its popup on Escape and leave the key unhandled: Button.OnKeyDown closes its Flyout so (Grok audit of
        // the Escape fix, P2). The window's tunnel, first on every route, notes whether a popup was open when the key started; the
        // bubble then ends such a key even when nothing is open any more, so a key that closed a popup never reaches cancel.
        key.AddClassHandler<Window>((window, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape && HasOpenPopup(window)) escapesOverPopup.AddOrUpdate(e, window);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        key.AddClassHandler<Window>((window, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape) return;
            // Both run: the mark of this key is always removed.
            if (CloseLatest(window) | escapesOverPopup.Remove(e)) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    // The Escape keys that started while a popup of their window was open (EscapeClosesPopupFirst), each key by itself: a key
    // raised while another is routed keeps its own mark; weak, so a key handled before the window's bubble holds neither itself
    // nor its window (Codex recheck 3, P3 ×2).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Avalonia.Interactivity.RoutedEventArgs, Window> escapesOverPopup = new();

    /// <summary>Whether a popup that takes Escape (not a tooltip) is open in <paramref name="window"/> or in a popup of it.</summary>
    public static bool HasOpenPopup(Window window) => Latest(window) is not null;

    // The open popups in the order they opened (EscapeClosesPopupFirst); a closed popup leaves the list.
    private static readonly List<Popup> openPopups = [];

    private static bool CloseLatest(StyledElement scope)
    {
        if (Latest(scope) is not { } popup) return false;
        // Closed as the popup's own light dismiss does: SetCurrentValue keeps the template's two-way binding to the control
        // (ComboBox.IsDropDownOpen). The IsOpen setter put a local value over it: the list then reopened as «open» with its popup
        // closed, the wheel went to that list and the next Escape closed nothing (Linux 6, Н1).
        popup.SetCurrentValue(Popup.IsOpenProperty, false);
        return true;
    }

    // The latest opened popup of the scope: one whose chain of hosts (OwnerOf) passes through it.
    private static Popup? Latest(StyledElement scope)
    {
        for (var i = openPopups.Count - 1; i >= 0; i--)
        {
            var popup = openPopups[i];
            // Its own tooltip popup (ToolTip.Open) keeps the old behaviour: Escape is not its key.
            if (!popup.IsOpen || popup.Child is ToolTip) continue;
            for (StyledElement? at = popup; at is not null; at = OwnerOf(at))
                if (at == scope) return popup;
            // Set open while in no window, it never opened: dropped from the list.
            if (WindowOf(popup) is null) openPopups.RemoveAt(i);
        }
        return null;
    }

    private static Window? WindowOf(Popup popup)
    {
        StyledElement? at = popup;
        while (at is Popup) at = OwnerOf(at);
        return at as Window;
    }

    // The next step up from a popup: the popup whose host (a native PopupRoot or an OverlayPopupHost) holds it, otherwise its
    // window. A popup without a visual parent (a context menu's) is placed by its target and belongs where the target is.
    private static StyledElement? OwnerOf(StyledElement element)
    {
        if (element is not Popup popup) return null;
        Visual? place = popup.GetVisualParent() is not null ? popup : popup.PlacementTarget;
        if (place is null) return null;
        foreach (var ancestor in place.GetVisualAncestors())
            if (ancestor is PopupRoot or OverlayPopupHost) return ((StyledElement)ancestor).Parent as Popup;
        return TopLevel.GetTopLevel(place) as Window;
    }

    private static void PassWheel(Control control, Avalonia.Input.PointerWheelEventArgs e, bool open)
    {
        // Only over the closed control itself: the popup's items are not its visual descendants.
        if (open || e.Source is not Visual source || source != control && !control.IsVisualAncestorOf(source) ||
            control.GetVisualParent() is not Avalonia.Interactivity.Interactive parent || TopLevel.GetTopLevel(control) is not { } root) return;
        e.Handled = true;
        parent.RaiseEvent(new Avalonia.Input.PointerWheelEventArgs(parent, e.Pointer, root, e.GetPosition(root), e.Timestamp, e.Properties,
            e.KeyModifiers, e.Delta));
    }

    /// <summary>A tooltip that also opens when the control gets the keyboard focus and closes when it loses it (Avalonia 12 opens
    /// tooltips on hover only), for the sentences moved from the page into the tip (Grok audit 11.11).</summary>
    public static T TipOnFocus<T>(T control) where T : Control
    {
        // Opened by the focus, the tip stands under the control, not at a mouse pointer left anywhere (Linux check 2); hover keeps
        // the usual place at the pointer.
        control.GotFocus += (_, e) =>
        {
            // Only the keyboard opens it: the field focused when its window opens (a group's name) would cover the row under it.
            if (ToolTip.GetTip(control) is null || e.NavigationMethod is not (Avalonia.Input.NavigationMethod.Tab or Avalonia.Input.NavigationMethod.Directional)) return;
            // Beside the field when the window has room on its right, so the tip covers no other row (З6 of the Linux check 12.11);
            // otherwise right under it, as close as the popup allows (Linux check 3).
            var room = TopLevel.GetTopLevel(control) is { } top && control.TranslatePoint(new Point(control.Bounds.Width, 0), top) is { } edge
                ? top.Bounds.Width - edge.X : 0;
            if (room >= 240) { ToolTip.SetPlacement(control, PlacementMode.Right); ToolTip.SetHorizontalOffset(control, 4); ToolTip.SetVerticalOffset(control, 0); }
            else { ToolTip.SetPlacement(control, PlacementMode.Bottom); control.ClearValue(ToolTip.HorizontalOffsetProperty); ToolTip.SetVerticalOffset(control, 0); }
            ToolTip.SetIsOpen(control, true);
        };
        void Hover() { ToolTip.SetPlacement(control, PlacementMode.Pointer); control.ClearValue(ToolTip.VerticalOffsetProperty); control.ClearValue(ToolTip.HorizontalOffsetProperty); }
        control.LostFocus += (_, _) => { ToolTip.SetIsOpen(control, false); Hover(); };
        control.PointerEntered += (_, _) => { if (!control.IsKeyboardFocusWithin) Hover(); };
        return control;
    }

    /// <summary>Gives the field rows of <paramref name="page"/> one caption column as wide as its longest caption (as the original's
    /// forms: «Общее» has its fields at L199 after captions up to 176 px). Measured each time the page is shown, with the fonts it
    /// inherits there.</summary>
    public static void AlignCaptions(Control page)
    {
        page.SetValue(ScopeProperty, new CaptionScope());
        page.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            double widest = 0;
            foreach (var row in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(page).OfType<Grid>().Where(x => x.Classes.Contains("field")))
            {
                if (row.Children.Count == 0) continue;
                var label = row.Children[0];
                label.Measure(Size.Infinity);
                widest = Math.Max(widest, label.DesiredSize.Width);
            }
            if (widest > 0) page.SetValue(CaptionWidthProperty, Math.Ceiling(widest) + 2);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The field rows of one page (<see cref="AlignCaptions"/>): whether any shown row needs its field under its caption.</summary>
    private sealed class CaptionScope
    {
        private readonly Dictionary<Grid, bool> needs = [];
        private bool stacked;
        public event Action? Changed;
        /// <summary>The row's own need; returns whether the page stacks. A change of the page's layout lays out all its rows again.</summary>
        public bool Report(Grid row, bool need)
        {
            needs[row] = need;
            var now = needs.Any(x => x.Value && x.Key.IsEffectivelyVisible && TopLevel.GetTopLevel(x.Key) is not null);
            if (now != stacked) { stacked = now; Changed?.Invoke(); }
            return stacked;
        }
    }
    private static readonly AttachedProperty<CaptionScope?> ScopeProperty =
        AvaloniaProperty.RegisterAttached<Control, CaptionScope?>("CaptionScope", typeof(DialogLayout), null, inherits: true);

    /// <summary>A caption above its field: for a wide field alone in its group, as the original's «Каталог по-умолчанию:».</summary>
    public static Control Above(string caption, Control field)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(Label(caption)); panel.Children.Add(field);
        return panel;
    }

    /// <summary>A check box (or another caption) and its field on one line, as the original's «Использовать хост: [ ]».</summary>
    public static Control Inline(Control first, Control field)
    {
        var row = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8 };
        first.VerticalAlignment = field.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(first); Grid.SetColumn(field, 1); row.Children.Add(field);
        // As Field: when the field would be left too narrow, it goes under the caption, which then wraps to the row (Codex audit 11.11).
        row.SizeChanged += (_, _) =>
        {
            // The caption on one line; measured apart from the layout, then measured again by it.
            first.Measure(Size.Infinity);
            var stacked = row.Bounds.Width - first.DesiredSize.Width - 8 < Math.Max(160, row.FontSizeForLayout() * 10);
            first.InvalidateMeasure();
            row.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            row.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(field, stacked ? 0 : 1); Grid.SetRow(field, stacked ? 1 : 0);
            row.RowSpacing = stacked ? 3 : 0; row.ColumnSpacing = stacked ? 0 : 8;
        };
        return row;
    }

    /// <summary>A group of a page as the original's forms draw it: a bold blue caption one point larger than the text, no line, the
    /// content indented under it. A null title is a group without a caption (the page's own title already names it).</summary>
    /// <param name="line">A line under the caption across the page, as the Bevel under the group captions of the base properties
    /// (smFormProperty); the settings have none.</param>
    /// <param name="indent">The indent of the content under its caption.</param>
    public static StackPanel Group(string? title, Control content, double fontSize, bool line = false, double indent = 16)
    {
        var panel = new StackPanel { Spacing = 6 };
        if (title is not null)
        {
            var caption = new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = fontSize + 1, Foreground = UiColors.SectionText,
                TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(line ? new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 3), Child = caption } : caption);
        }
        content.Margin = new Thickness(title is null ? 0 : indent, 0, 0, 0);
        panel.Children.Add(content);
        return panel;
    }

    /// <summary>The groups of a page, 14 apart.</summary>
    public static StackPanel Groups(params Control[] groups)
    {
        var panel = new StackPanel { Spacing = 14 };
        foreach (var group in groups) panel.Children.Add(group);
        return panel;
    }

    /// <summary>The rows of a group: fields, check boxes and notes 6 apart.</summary>
    public static StackPanel Rows() => new() { Spacing = 6 };

    /// <summary>Field rows in two columns, as the original's «Пользователь: [ ] Пароль: [ ]» and the proxy fields 2×2; each column has
    /// its own caption column. The keyboard goes down the left column, then the right one (the original's TabOrder). A row narrower
    /// than two columns of <paramref name="minColumn"/> puts all the rows in one column row by row (Адрес, Порт, Пользователь, Пароль:
    /// З4 of the Linux check 12.11), with one caption column for them, and the keyboard follows that order.</summary>
    /// <remarks>The rows stay children of the one grid; only their cells and tab indexes change (reparenting them lost the focus of the
    /// edited field and the grey of disabled captions, Codex recheck and Linux check 2 of 12.11). The captions are measured once the
    /// grid is laid out in the dialog, with its fonts and styles (measured before, a caption came out too narrow and its colon wrapped).</remarks>
    public static Grid Columns(IReadOnlyList<Control> left, IReadOnlyList<Control> right, double minColumn)
    {
        var count = Math.Max(left.Count, right.Count);
        var grid = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12, RowSpacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch,
            // Two star columns never collapse to nothing, whatever width their parent measures them with (Grok audit 12.11, P1).
            MinWidth = Math.Round(minColumn / 2) };
        for (var i = 0; i < 2 * count; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        // Column by column, so the wide layout keeps the original's TabOrder; the rows' tab indexes order the keyboard inside the grid.
        foreach (var row in left.Concat(right)) grid.Children.Add(row);
        Avalonia.Input.KeyboardNavigation.SetTabNavigation(grid, Avalonia.Input.KeyboardNavigationMode.Local);
        static double Caption(Control row)
        {
            if (row is not Grid { Children.Count: > 0 } field || !field.Classes.Contains("field")) return 0;
            var label = field.Children[0]; label.Measure(Size.Infinity); var width = label.DesiredSize.Width; label.InvalidateMeasure();
            return width;
        }
        bool? shown = null; double fontShown = 0;
        void Arrange(bool stacked)
        {
            for (var i = 0; i < count; i++)
            {
                if (i < left.Count) Place(left[i], stacked ? 2 * i : i, 0, stacked ? 2 * i : i);
                if (i < right.Count) Place(right[i], stacked ? 2 * i + 1 : i, stacked ? 0 : 1, stacked ? 2 * i + 1 : count + i);
            }
            grid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            grid.ColumnSpacing = stacked ? 0 : 12;
            double leftWidth = Math.Ceiling(left.Select(Caption).DefaultIfEmpty(0).Max()) + 2, rightWidth = Math.Ceiling(right.Select(Caption).DefaultIfEmpty(0).Max()) + 2;
            foreach (var row in left) row.SetValue(CaptionWidthProperty, stacked ? Math.Max(leftWidth, rightWidth) : leftWidth);
            foreach (var row in right) row.SetValue(CaptionWidthProperty, stacked ? Math.Max(leftWidth, rightWidth) : rightWidth);
        }
        static void Place(Control row, int at, int column, int tab)
        {
            Grid.SetRow(row, at); Grid.SetColumn(row, column);
            Avalonia.Input.KeyboardNavigation.SetTabIndex(row, tab);
            // And its field: the index of the row container alone does not order the focusable controls in it.
            if (row is Grid { Children.Count: > 1 } field) Avalonia.Input.KeyboardNavigation.SetTabIndex(field.Children[1], tab);
        }
        Arrange(false);
        grid.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged || e.NewSize.Width <= 0) return;
            var stacked = e.NewSize.Width < 2 * minColumn + 12;
            var font = grid.GetValue(TextBlock.FontSizeProperty);
            if (shown == stacked && fontShown == font) return;
            shown = stacked; fontShown = font;
            // After this layout pass, when the captions have the dialog's fonts.
            Dispatcher.UIThread.Post(() => { if (shown == stacked) Arrange(stacked); }, DispatcherPriority.Loaded);
        };
        return grid;
    }

    /// <summary>The list of the pages of a dialog as the original's ButtonGroup (smFormProperty.bgrSections): items about a field
    /// high and the chosen one as a pressed button, a neutral face with a frame instead of the accent fill.</summary>
    public static ListBox Sections(ListBox list, UiTypography type)
    {
        list.Resources["ListBoxItemPadding"] = new Thickness(8, 2);
        list.Styles.Add(new Style(s => s.OfType<ListBoxItem>()) { Setters = { new Setter(Layoutable.MinHeightProperty, type.ControlHeight + 6) } });
        foreach (var state in new[] { ":selected", ":selected:pointerover", ":selected:pressed", ":selected:focus" })
            list.Styles.Add(new Style(s => Classes(s.OfType<ListBoxItem>(), state).Template().OfType<ContentPresenter>())
            { Setters = { new Setter(ContentPresenter.BackgroundProperty, UiColors.Button), new Setter(ContentPresenter.ForegroundProperty, UiColors.Text),
                new Setter(ContentPresenter.BorderBrushProperty, UiColors.Line), new Setter(ContentPresenter.BorderThicknessProperty, new Thickness(1)) } });
        return list;
    }
    private static Selector Classes(Selector selector, string states)
    {
        foreach (var state in states.Split(':', StringSplitOptions.RemoveEmptyEntries)) selector = selector.Class(":" + state);
        return selector;
    }

    /// <summary>The rows of a dialog's tree as the original's trees: a line and a little room, the levels about 1.35 × the font apart.</summary>
    public static void CompactTree(Control tree, UiTypography type)
    {
        tree.Resources["TreeViewItemMinHeight"] = Math.Ceiling(UiTypography.Measure("Йру", type.DialogFamily, type.DialogSize).Height) + 5;
        tree.Resources["TreeViewItemIndent"] = Math.Round(type.DialogSize * 1.35);
    }

    /// <summary>The bottom bar of a dialog with a message (the settings and the base properties): <paramref name="left"/> and
    /// <paramref name="right"/> are its buttons; the message stands between them, or above them when the bar is narrow. It is one
    /// line, shortened with «…»; «Подробнее…» (a button, so the keyboard reaches it) then shows the whole text in a window where it
    /// can be selected. The bar keeps its height whatever the message, so a message does not move the page (Linux check and Codex
    /// final recheck 11.11). <paramref name="whole"/> gives the whole text of a summary (always offered by «Подробнее…»).</summary>
    public static Grid Footer(Window owner, TextBlock message, Control left, Control right, Func<UiTypography> type, Func<string, string?>? whole = null)
    {
        message.TextWrapping = TextWrapping.NoWrap; message.TextTrimming = TextTrimming.CharacterEllipsis; message.VerticalAlignment = VerticalAlignment.Center;
        // UiTypography.ApplyDialog wraps every text of a dialog after it is built: this one stays a line.
        message.PropertyChanged += (_, e) =>
        { if (e.Property == TextBlock.TextWrappingProperty && message.TextWrapping != TextWrapping.NoWrap) message.TextWrapping = TextWrapping.NoWrap; };
        string Whole(string text) => whole?.Invoke(text) ?? text;
        var details = SettingsWindow.Command("Подробнее…", async () => await ShowMessage(owner, Whole(message.Text ?? ""), message.Foreground, type()));
        details.IsVisible = false; details.Margin = default; details.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(details, "Показать сообщение полностью");
        var messages = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 };
        messages.Children.Add(message); Grid.SetColumn(details, 1); messages.Children.Add(details);
        void Shortened()
        {
            var text = message.Text ?? "";
            var width = text.Length == 0 ? 0 : new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(message.FontFamily, message.FontStyle, message.FontWeight), message.FontSize, Brushes.Black).Width;
            var room = messages.Bounds.Width - (details.IsVisible ? details.Bounds.Width + 6 : 0);
            var cut = text.Length > 0 && (whole?.Invoke(text) is not null || room > 0 && width > room + 0.5);
            if (details.IsVisible != cut) details.IsVisible = cut;
            ToolTip.SetTip(message, cut ? Whole(text) : null);
        }
        message.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) Dispatcher.UIThread.Post(Shortened, DispatcherPriority.Loaded); };
        messages.SizeChanged += (_, e) => { if (e.WidthChanged) Shortened(); };
        var footer = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        // The message line first: Tab goes message → «Подробнее…» → the buttons, as they stand.
        footer.Children.Add(messages);
        left.VerticalAlignment = right.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetRow(left, 1); footer.Children.Add(left);
        Grid.SetRow(right, 1); Grid.SetColumn(right, 2); footer.Children.Add(right);
        void Arrange()
        {
            if (footer.Bounds.Width <= 0) return;
            var free = footer.Bounds.Width - left.Bounds.Width - right.Bounds.Width - 16;
            var inline = free >= message.FontSize * 18;
            Grid.SetRow(messages, inline ? 1 : 0); Grid.SetColumn(messages, inline ? 1 : 0); Grid.SetColumnSpan(messages, inline ? 1 : 3);
            messages.Margin = inline ? new Thickness(4, 0) : new Thickness(0, 0, 0, 4);
            // The message line is always there (empty or not), one line high with room for «Подробнее…»: beside the buttons it takes
            // no height of its own, above them one line and a small gap; so the page keeps its bounds in every state.
            var line = Math.Max(Math.Ceiling(UiTypography.Measure("Йру", message.FontFamily, message.FontSize).Height), type().ControlHeight);
            messages.Height = line;
            var buttons = Math.Max(left.Bounds.Height, right.Bounds.Height);
            var height = inline ? Math.Max(buttons, line) : buttons + line + 4;
            if (Math.Abs(footer.MinHeight - height) > 0.5) footer.MinHeight = height;
        }
        // Its buttons get their final size (the dialog font, applied after the bar is built) after the bar has its width: the bar is
        // arranged again then, or the buttons stayed below the window at font 22 until a page switch (Codex audit 12.11, P3).
        footer.SizeChanged += (_, _) => Arrange();
        left.SizeChanged += (_, _) => Arrange(); right.SizeChanged += (_, _) => Arrange();
        return footer;
    }

    /// <summary>«Подробнее…» of a bottom bar: the whole message in a small window, selectable, closed by OK or Esc.</summary>
    public static async Task ShowMessage(Window owner, string message, IBrush? foreground, UiTypography type)
    {
        var box = new Window { Title = "Сообщение", Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false };
        var text = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = foreground };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var ok = SettingsWindow.Command("OK", box.Close); ok.IsDefault = ok.IsCancel = true; ok.Margin = default; buttons.Children.Add(ok);
        box.Content = Frame(text, buttons);
        type.ApplyDialog(box);
        text.TextWrapping = TextWrapping.Wrap;
        box.Opened += (_, _) => ok.Focus();
        await box.ShowDialog(owner);
    }

    /// <summary>The one short note of a group: grey and one point smaller, as the original's notes (lblUpdNote, Label26).</summary>
    public static TextBlock Note(string text, double fontSize) => new()
    { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = UiColors.Muted, FontSize = Math.Max(10, fontSize - 1) };

    /// <summary>A path field as the original's buttoned edits: a folder or file icon at its left and «…» inside at its right.</summary>
    public static TextBox PathBox(TextBox box, string icon, double iconSize, string tip, Func<Task> pick)
    {
        var glyph = UiIcons.Create(icon, iconSize);
        glyph.Margin = new Thickness(6, 0, 0, 0); glyph.VerticalAlignment = VerticalAlignment.Center;
        box.InnerLeftContent = glyph;
        box.InnerRightContent = Ellipsis(tip, pick);
        return box;
    }

    /// <summary>An icon in place of the text of a command button, as the original's square list buttons (flat: its tool bars).
    /// The tip names the command and is its accessible name; a disabled button shows its icon faded.</summary>
    public static Button Icon(Button button, string icon, string tip, double size, bool flat = false)
    {
        var glyph = UiIcons.Create(icon, size);
        button.Content = glyph; button.MinWidth = 0; button.MinHeight = 0;
        button.Padding = new Thickness(Math.Round(Math.Max(3, size / 4)));
        if (flat) { button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); }
        // A command keeps the accessible name of its caption («Удалить»); the tip may say more («Удалить сценарий · Del»).
        ToolTip.SetTip(button, tip); ToolTip.SetShowOnDisabled(button, true);
        if (string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(button))) Avalonia.Automation.AutomationProperties.SetName(button, tip);
        // A disabled icon stays recognisable: its glyph faded on the usual face, not an empty grey square (Linux check 11.11).
        button.Resources["ButtonBackgroundDisabled"] = flat ? Brushes.Transparent : UiColors.Button;
        button.Resources["ButtonBorderBrushDisabled"] = flat ? Brushes.Transparent : UiColors.Line;
        void Fade() => glyph.Opacity = button.IsEffectivelyEnabled ? 1 : .45;
        button.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Input.InputElement.IsEffectivelyEnabledProperty) Fade(); };
        Fade();
        return button;
    }

    /// <summary>The items of a drop-down list on one line, shortened with «…» when the list is narrower (a wrapped item broke
    /// words in the middle on Linux, DejaVu Sans; Linux check 11.11).</summary>
    public static Avalonia.Controls.Templates.FuncDataTemplate<object> OneLine { get; } = new((value, _) =>
    {
        var text = new TextBlock { Text = value?.ToString() ?? "", TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        // A value shortened with «…» (the closed list at a large font) shows whole in its tooltip.
        text.SizeChanged += (_, e) =>
        {
            var whole = new FormattedText(text.Text ?? "", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight), text.FontSize, Brushes.Black).Width;
            ToolTip.SetTip(text, whole > e.NewSize.Width + 0.5 ? text.Text : null);
        };
        return text;
    });

    /// <summary>The selected item of a tree or list as the main list's: the yellow <see cref="UiColors.Selection"/>.</summary>
    public static void YellowSelection(Control control)
    {
        // The tree takes its colours from resources; the list item of Fluent paints its presenter, so a style sets it, as the main list.
        foreach (var state in new[] { "Selected", "SelectedPointerOver", "SelectedPressed" })
        {
            control.Resources["TreeViewItemBackground" + state] = UiColors.Selection;
            control.Resources["TreeViewItemForeground" + state] = UiColors.SelectedText;
        }
        control.Styles.Add(new Style(s => s.OfType<ListBoxItem>().Class(":selected").Template().OfType<ContentPresenter>())
        { Setters = { new Setter(ContentPresenter.BackgroundProperty, UiColors.Selection), new Setter(ContentPresenter.ForegroundProperty, UiColors.SelectedText) } });
    }

    /// <summary>The compact dialog (decision of the user 11.11.2026): fields, lists and spin boxes about 1.75 × the dialog font high
    /// with a small padding, check boxes and radio buttons with a 13–14 px box in a row a little lower, as the original's forms.
    /// Everything follows the dialog font, so a large font still gets large controls. The Fluent parts are sized by their template;
    /// a selector with a state is a style trigger and wins over it (as the compact radio buttons of the main window).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, Styles> CompactStyles = new();
    public static void Compact(Control root, UiTypography type)
    {
        // Applied again after a font change (the settings window): the styles of the previous font are replaced, not stacked.
        if (CompactStyles.TryGetValue(root, out var previous)) root.Styles.Remove(previous);
        var styles = new Styles();
        root.Styles.Add(styles); CompactStyles.AddOrUpdate(root, styles);
        var resources = root.Resources;
        resources["TextControlThemeMinHeight"] = type.ControlHeight; resources["ComboBoxMinHeight"] = type.ControlHeight;
        resources["TextControlThemePadding"] = new Thickness(6, 2, 6, 2); resources["ComboBoxPadding"] = new Thickness(8, 1, 0, 1);
        resources["ButtonPadding"] = new Thickness(8, 2, 8, 3);
        resources["CheckBoxMinHeight"] = type.CheckRowHeight; resources["RadioButtonMinHeight"] = type.CheckRowHeight;
        // Disabled fields, lists and dates as the original's: the face of the window with a thin edge and grey text, not the heavy
        // grey slabs of Fluent (step 3a, 12.11).
        foreach (var part in new[] { "TextControl", "ComboBox", "CalendarDatePicker" })
        {
            resources[part + "BackgroundDisabled"] = UiColors.Surface; resources[part + "BorderBrushDisabled"] = UiColors.Line;
            resources[part + "ForegroundDisabled"] = UiColors.Muted;
        }
        resources["TextControlPlaceholderForegroundDisabled"] = resources["CalendarDatePickerTextForegroundDisabled"] = UiColors.Muted;
        resources["CalendarDatePickerCalendarGlyphForegroundDisabled"] = resources["ComboBoxDropDownGlyphForegroundDisabled"] = UiColors.DisabledMark;
        // A disabled button keeps its face; its caption is grey, as a disabled standard button.
        resources["ButtonBackgroundDisabled"] = UiColors.Button; resources["ButtonBorderBrushDisabled"] = UiColors.Line;
        resources["ButtonForegroundDisabled"] = UiColors.DisabledText;
        // A slider in a row as high as a field, its thumb about the size of a check box.
        var thumb = type.CheckSize + 2;
        resources["SliderHorizontalHeight"] = type.ControlHeight; resources["SliderHorizontalThumbWidth"] = thumb; resources["SliderHorizontalThumbHeight"] = thumb;
        resources["SliderThumbCornerRadius"] = new CornerRadius(thumb / 2);
        resources["SliderPreContentMargin"] = resources["SliderPostContentMargin"] = new GridLength(Math.Max(0, Math.Floor((type.ControlHeight - thumb) / 2)));
        var box = type.CheckSize; var gap = new Thickness(Math.Round(Math.Max(4, type.DialogSize / 3)), 0, 0, 0);
        foreach (var state in new[] { ":checked", ":unchecked", ":indeterminate" })
        {
            styles.Add(new Style(s => s.OfType<CheckBox>().Class(state).Template().OfType<Border>().Name("NormalRectangle"))
            { Setters = { new Setter(Layoutable.WidthProperty, box), new Setter(Layoutable.HeightProperty, box) } });
            styles.Add(new Style(s => s.OfType<CheckBox>().Class(state).Template().OfType<Viewbox>())
            { Setters = { new Setter(Layoutable.WidthProperty, box), new Setter(Layoutable.HeightProperty, box) } });
            foreach (var (part, size) in new[] { ("OuterEllipse", box), ("CheckOuterEllipse", box), ("CheckGlyph", Math.Round(box / 2.3)) })
                styles.Add(new Style(s => s.OfType<RadioButton>().Class(state).Template().Name(part))
                { Setters = { new Setter(Layoutable.WidthProperty, size), new Setter(Layoutable.HeightProperty, size) } });
        }
        // The spin boxes: the two arrows stacked at the right edge, as the original's TUpDown, instead of two 34 px buttons side by side.
        var half = Math.Floor((type.ControlHeight - 2) / 2);
        styles.Add(new Style(s => s.OfType<ButtonSpinner>().Class(":right").Template().OfType<StackPanel>().Name("PART_SpinnerPanel"))
        { Setters = { new Setter(StackPanel.OrientationProperty, Orientation.Vertical) } });
        styles.Add(new Style(s => s.OfType<ButtonSpinner>().Class(":right").Template().OfType<RepeatButton>())
        { Setters = { new Setter(Layoutable.MinWidthProperty, Math.Round(type.DialogSize * 1.6)), new Setter(Layoutable.HeightProperty, half),
            new Setter(Layoutable.MinHeightProperty, 0d), new Setter(TemplatedControl.PaddingProperty, new Thickness(0)) } });
        styles.Add(new Style(s => s.OfType<ButtonSpinner>().Class(":right").Template().OfType<PathIcon>())
        { Setters = { new Setter(Layoutable.WidthProperty, Math.Round(type.DialogSize * .7)), new Setter(Layoutable.HeightProperty, Math.Round(type.DialogSize * .35)) } });
        // Placeholders shortened with «…» instead of cut at the field's edge. The placeholder lies in the field's horizontally scrolling
        // content and was measured wider than its viewport, so the trimming never applied (Codex recheck 11.11): while the field is
        // empty it does not scroll horizontally; typed text scrolls as before.
        styles.Add(new Style(s => s.OfType<TextBox>().Class(":empty"))
        { Setters = { new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled) } });
        styles.Add(new Style(s => s.OfType<TextBox>().Template().OfType<TextBlock>().Name("PART_Placeholder"))
        { Setters = { new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis) } });
        styles.Add(new Style(s => s.OfType<ComboBox>().Template().OfType<TextBlock>().Name("PlaceholderTextBlock"))
        { Setters = { new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis) } });
        // The captions of disabled fields grey, as the original's disabled labels (a local colour, as an error's, stays).
        styles.Add(new Style(s => s.OfType<TextBlock>().Class(":disabled")) { Setters = { new Setter(TextBlock.ForegroundProperty, UiColors.DisabledText) } });
        styles.Add(new Style(s => s.OfType<CheckBox>()) { Setters = { new Setter(TemplatedControl.PaddingProperty, gap) } });
        // The tooltips in the dialog font too, so a large font enlarges them (З2 of the Linux check 12.11).
        styles.Add(new Style(s => s.OfType<ToolTip>())
        { Setters = { new Setter(TemplatedControl.FontSizeProperty, type.DialogSize), new Setter(TemplatedControl.FontFamilyProperty, type.DialogFamily) } });
        styles.Add(new Style(s => s.OfType<RadioButton>()) { Setters = { new Setter(TemplatedControl.PaddingProperty, gap) } });
    }

    private static double FontSizeForLayout(this Control control) => control.GetValue(TextBlock.FontSizeProperty);

    public static Control Section(string title, Control content)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 5), Child = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold,
                Foreground = UiColors.SectionText, TextWrapping = TextWrapping.Wrap } });
        panel.Children.Add(content);
        return panel;
    }

    // Buttons usually stay together on the left; a DockPanel spreads them to both edges.
    /// <param name="line">The line over the buttons; a small dialog of the original without its Bevel (FormGroupProp) has none.</param>
    public static Grid Frame(Control body, Panel buttons, TextBlock? error = null, bool scrollBody = true, bool line = true)
    {
        foreach (var button in buttons.Children.OfType<Button>()) button.MinWidth = 90;
        var root = new Grid { Margin = new Thickness(12), RowDefinitions = new("*,Auto,Auto"), RowSpacing = 8 };
        root.Children.Add(scrollBody ? new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } : body);
        if (error is not null)
        {
            var messages = new ScrollViewer { Content = error, MaxHeight = 90, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            // An empty message row would still take a line of height above the buttons.
            messages.IsVisible = !string.IsNullOrEmpty(error.Text);
            error.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) messages.IsVisible = !string.IsNullOrEmpty(error.Text); };
            Grid.SetRow(messages, 1); root.Children.Add(messages);
        }
        var footer = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, line ? 1 : 0, 0, 0),
            Padding = new Thickness(0, line ? 8 : 4, 0, 0), Child = buttons };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        return root;
    }
}
