using System.Windows;
using System.Windows.Controls;

namespace AoTPresetEditor;

internal static class EditorControls
{
    public static void FitToOwner(Window window, double ownerFraction = 0.94, double screenFraction = 0.85)
    {
        window.SourceInitialized += (_, _) =>
        {
            const double edgeInset = 24;
            var owner = window.Owner;
            var hasOwnerSize = owner is { ActualWidth: > 0, ActualHeight: > 0 };
            var bounds = hasOwnerSize ? new Size(owner!.ActualWidth, owner.ActualHeight) : SystemParameters.WorkArea.Size;
            var width = Math.Max(1, bounds.Width - edgeInset);
            var height = Math.Max(1, bounds.Height - edgeInset);
            var fraction = hasOwnerSize ? ownerFraction : screenFraction;
            window.MinWidth = Math.Min(window.MinWidth, width);
            window.MinHeight = Math.Min(window.MinHeight, height);
            window.Width = Math.Clamp(bounds.Width * fraction, window.MinWidth, width);
            window.Height = Math.Clamp(bounds.Height * fraction, window.MinHeight, height);
        };
    }

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
