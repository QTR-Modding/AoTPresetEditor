using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AoTPresetEditor;

internal static class EditorControls
{
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(91, 104, 123));
    public static TextBlock Text(string text, bool heading = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        FontSize = heading ? 20 : 14, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal
    };
    public static Button Button(string title, Action action)
    {
        var button = new Button { Content = title };
        button.Click += (_, _) => action();
        return button;
    }
    public static DockPanel Footer(Panel parent, params Button[] buttons)
    {
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        foreach (var button in buttons.Reverse()) { DockPanel.SetDock(button, Dock.Right); footer.Children.Add(button); }
        parent.Children.Add(footer);
        return footer;
    }
}
