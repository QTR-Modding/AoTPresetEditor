using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AoTPresetEditor;

internal static class EditorControls
{
    public static DrawingBrush Checkerboard()
    {
        const double size = 8;
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.WhiteSmoke, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        drawing.Children.Add(new GeometryDrawing(Brushes.LightGray, null, new RectangleGeometry(new Rect(0, 0, size / 2, size / 2))));
        drawing.Children.Add(new GeometryDrawing(Brushes.LightGray, null, new RectangleGeometry(new Rect(size / 2, size / 2, size / 2, size / 2))));
        return new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, size, size) };
    }

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
    public static Button Button(string title, Action action, bool isCancel = false, bool isDefault = false)
    {
        var button = new Button { Content = title, IsCancel = isCancel, IsDefault = isDefault };
        button.Click += (_, _) => action();
        return button;
    }
    public static Button IconButton(string glyph, string label, Action action)
    {
        var button = Button("", action);
        button.Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16 };
        button.Width = button.Height = 32;
        button.MinWidth = button.MinHeight = 0;
        button.Padding = new Thickness(0); button.Margin = new Thickness(0, 0, 4, 0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.ToolTip = label;
        AutomationProperties.SetName(button, label);
        ToolTipService.SetShowOnDisabled(button, true);
        return button;
    }
    public static Button Info(string label, string explanation)
    {
        var tooltip = new ToolTip { Content = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            Placement = PlacementMode.Bottom };
        var button = new Button { Content = "ⓘ", Width = 24, Height = 24, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip };
        tooltip.PlacementTarget = button;
        AutomationProperties.SetName(button, "Help for " + label);
        AutomationProperties.SetHelpText(button, explanation);
        ToolTipService.SetInitialShowDelay(button, 0);
        button.Click += (_, _) => tooltip.IsOpen = true;
        button.GotKeyboardFocus += (_, _) => tooltip.IsOpen = true;
        button.LostKeyboardFocus += (_, _) => tooltip.IsOpen = false;
        button.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || !tooltip.IsOpen) return;
            tooltip.IsOpen = false; args.Handled = true;
        };
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
