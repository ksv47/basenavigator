using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

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
            BorderThickness = new Thickness(0), Padding = new Thickness(5, 1), VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(button, name); ToolTip.SetTip(button, name);
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>A field whose caption is itself a control, e.g. the check box that enables it.</summary>
    public static Control Field(Control label, Control field)
    {
        label.VerticalAlignment = VerticalAlignment.Center;
        field.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = new("150,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 10 };
        row.Children.Add(label); row.Children.Add(field); Grid.SetColumn(field, 1);
        row.SizeChanged += (_, _) =>
        {
            var stacked = row.Bounds.Width < 440 || row.FontSizeForLayout() > 18;
            row.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(150);
            row.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(field, stacked ? 0 : 1); Grid.SetRow(field, stacked ? 1 : 0);
            // The empty second column would still keep the spacing and make a stacked field narrower than its neighbours.
            row.RowSpacing = stacked ? 5 : 0; row.ColumnSpacing = stacked ? 0 : 10;
        };
        return row;
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
    public static Grid Frame(Control body, Panel buttons, TextBlock? error = null, bool scrollBody = true)
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
        var footer = new Border { BorderBrush = UiColors.Line, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0), Child = buttons };
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        return root;
    }
}
