using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AtFutureMe.Services;

namespace AtFutureMe.Windows;

static class FocusWindowPicker
{
    public static LinkFocus.FocusChoice? Prompt(Window owner)
    {
        var choices = LinkFocus.ListChoices();
        var dialog = new Window
        {
            Title = "Focus window",
            Width = 520,
            Height = 460,
            MinWidth = 360,
            MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = Freeze(0x20, 0x22, 0x28),
            Foreground = Freeze(0xE8, 0xEA, 0xED),
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = false
        };

        var list = new ListBox
        {
            Margin = new Thickness(12, 8, 12, 0),
            Background = Freeze(0x2A, 0x2D, 0x34),
            Foreground = Freeze(0xE8, 0xEA, 0xED),
            BorderThickness = new Thickness(0)
        };
        foreach (var choice in choices)
            list.Items.Add(new Row(choice));

        var hint = new TextBlock
        {
            Text = choices.Count == 0
                ? "No other windows are open. Open the game, then pick again."
                : "Pick the window this link should bring forward.",
            Margin = new Thickness(12, 12, 12, 0),
            Foreground = Freeze(0x9A, 0xA0, 0xA8),
            TextWrapping = TextWrapping.Wrap
        };

        var use = new Button
        {
            Content = "Use this window",
            IsEnabled = false,
            IsDefault = true,
            MinWidth = 130,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 4, 10, 4)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 80,
            Padding = new Thickness(10, 4, 10, 4)
        };
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is Row)
                dialog.DialogResult = true;
        };
        list.SelectionChanged += (_, _) => use.IsEnabled = list.SelectedItem is Row;
        use.Click += (_, _) => dialog.DialogResult = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12)
        };
        buttons.Children.Add(use);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        root.Children.Add(list);
        dialog.Content = root;

        if (dialog.ShowDialog() == true && list.SelectedItem is Row picked)
            return picked.Choice;
        return null;
    }

    static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    sealed class Row
    {
        public LinkFocus.FocusChoice Choice { get; }
        public Row(LinkFocus.FocusChoice choice) => Choice = choice;
        public override string ToString() => $"{Choice.Title}  —  {Choice.ExeName}";
    }
}
