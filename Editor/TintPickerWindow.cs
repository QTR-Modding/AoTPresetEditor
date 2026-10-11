using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AoTPresetEditor;

public sealed class TintPickerWindow : Window
{
    private const uint RgbMaximum = 0xFFFFFF;
    private readonly Slider red = Channel("Red");
    private readonly Slider green = Channel("Green");
    private readonly Slider blue = Channel("Blue");
    private readonly Slider opacity = Channel("Opacity");
    private readonly CheckBox noTint = new() { Content = "No tint", Margin = new Thickness(0, 0, 0, 12) };
    private readonly Border preview = new();
    private readonly WrapPanel palette = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock status = EditorControls.Text("");
    public string? SelectedValue { get; private set; }

    public TintPickerWindow(string current)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "Choose tint"; Width = 420; MinWidth = 300;
        SizeToContent = SizeToContent.Height; MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var color = TryRead(current, out var existing, out var clear) && !clear ? existing : Colors.White;
        red.Value = color.R; green.Value = color.G; blue.Value = color.B; opacity.Value = color.A;
        noTint.IsChecked = clear;
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = new StackPanel();
        layout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(noTint);
        preview.Height = 58; preview.CornerRadius = new CornerRadius(4);
        var previewGrid = new Grid { Background = EditorControls.Checkerboard(), Margin = new Thickness(0, 0, 0, 12) };
        previewGrid.Children.Add(preview); panel.Children.Add(previewGrid);
        foreach (var (name, value) in new (string, Color)[]
        {
            ("White", Colors.White), ("Black", Colors.Black), ("Gray", Colors.Gray), ("Red", Colors.Red), ("Orange", Colors.Orange),
            ("Yellow", Colors.Yellow), ("Green", Colors.Lime), ("Cyan", Colors.Cyan), ("Blue", Colors.Blue),
            ("Violet", Colors.Violet), ("Pink", Colors.HotPink)
        })
        {
            var choice = EditorControls.Button("", () => { red.Value = value.R; green.Value = value.G; blue.Value = value.B; });
            choice.Content = new Border { Background = new SolidColorBrush(value), Width = 20, Height = 20 };
            choice.Padding = new Thickness(3); choice.Margin = new Thickness(0, 0, 4, 4); choice.ToolTip = name;
            AutomationProperties.SetName(choice, name); palette.Children.Add(choice);
        }
        panel.Children.Add(palette);
        foreach (var (name, slider) in new[] { ("Red", red), ("Green", green), ("Blue", blue), ("Opacity", opacity) })
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var label = new TextBlock { Text = name, Width = 58, VerticalAlignment = VerticalAlignment.Center };
            var number = new TextBlock { Width = 30, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            if (slider == opacity)
            {
                number.Width = 40; number.Text = $"{opacity.Value / byte.MaxValue:P0}";
                opacity.ValueChanged += (_, _) => number.Text = $"{opacity.Value / byte.MaxValue:P0}";
            }
            else number.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Value") { Source = slider, StringFormat = "F0" });
            DockPanel.SetDock(label, Dock.Left); row.Children.Add(label);
            DockPanel.SetDock(number, Dock.Right); row.Children.Add(number); row.Children.Add(slider); panel.Children.Add(row);
        }
        panel.Children.Add(status);
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var apply = EditorControls.Button("Apply", () =>
        {
            SelectedValue = noTint.IsChecked == true ? "0" : Encode(Selection);
            DialogResult = true;
        }, isDefault: true);
        apply.SetResourceReference(StyleProperty, "PrimaryButton");
        var footer = EditorControls.Footer(layout, apply, EditorControls.Button("Cancel", () => DialogResult = false, isCancel: true));
        Grid.SetRow(footer, 1); Content = layout;
        red.ValueChanged += (_, _) => Refresh(); green.ValueChanged += (_, _) => Refresh(); blue.ValueChanged += (_, _) => Refresh();
        opacity.ValueChanged += (_, _) => Refresh();
        noTint.Checked += (_, _) => Refresh(); noTint.Unchecked += (_, _) => Refresh();
        Refresh();
    }

    private Color Selection => Color.FromArgb((byte)opacity.Value, (byte)red.Value, (byte)green.Value, (byte)blue.Value);

    private void Refresh()
    {
        var clear = noTint.IsChecked == true;
        red.IsEnabled = green.IsEnabled = blue.IsEnabled = opacity.IsEnabled = palette.IsEnabled = !clear;
        preview.Background = clear ? Brushes.Transparent : new SolidColorBrush(Selection);
        status.Text = clear ? "No tint" : Encode(Selection);
    }

    private static Slider Channel(string name)
    {
        var slider = new Slider { Minimum = byte.MinValue, Maximum = byte.MaxValue, TickFrequency = 1, IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(slider, name); return slider;
    }

    public static Button CreateButton(TextBox entry)
    {
        var swatch = new Border { Width = 24, Height = 24, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray,
            Background = EditorControls.Checkerboard() };
        var colorFill = new Border(); var overlay = new Grid(); overlay.Children.Add(colorFill);
        var mark = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.Black, FontSize = 18, FontWeight = FontWeights.Bold };
        overlay.Children.Add(mark); swatch.Child = overlay;
        var button = EditorControls.Button("", () =>
        {
            var picker = new TintPickerWindow(entry.Text) { Owner = GetWindow(entry) };
            if (picker.ShowDialog() == true && picker.SelectedValue is { } selected) entry.Text = selected;
        });
        button.Content = swatch; button.Padding = new Thickness(5); button.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(button, "Choose tint");
        void RefreshSwatch()
        {
            var valid = TryRead(entry.Text, out var color, out var clear);
            var unset = string.IsNullOrWhiteSpace(entry.Text);
            colorFill.Background = valid && !clear ? new SolidColorBrush(color) : Brushes.Transparent;
            mark.Text = clear ? "∕" : valid || unset ? "" : "?";
            button.ToolTip = clear ? "No tint. Choose a color." : unset ? "Tint not set. Choose a color."
                : valid ? "Choose tint: " + entry.Text : "Invalid tint. Choose a color.";
        }
        entry.TextChanged += (_, _) => RefreshSwatch(); RefreshSwatch(); return button;
    }

    public static bool TryRead(string text, out Color color, out bool noTint)
    {
        color = default; noTint = false;
        text = text.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
            if (text.Length is not (6 or 8) || !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var explicitValue)) return false;
            color = text.Length == 8
                ? Color.FromArgb((byte)explicitValue, (byte)(explicitValue >> 24), (byte)(explicitValue >> 16), (byte)(explicitValue >> 8))
                : Color.FromRgb((byte)(explicitValue >> 16), (byte)(explicitValue >> 8), (byte)explicitValue);
            return true;
        }
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return false;
        noTint = value == 0;
        color = value > RgbMaximum
            ? Color.FromArgb((byte)value, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8))
            : Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public static string Encode(Color color) => color.A == byte.MaxValue
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";

}
