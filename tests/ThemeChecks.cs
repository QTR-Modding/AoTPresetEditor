using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AoTPresetEditor;

internal static class ThemeChecks
{
    private static readonly FormRow[] Forms =
    [new("Raw Beef", "FoodBeef", "Ingestible", "Skyrim.esm", "Skyrim.esm", "0x65C99~Skyrim.esm")];

    public static void Run()
    {
        Require(Application.Current is null, "Theme checks need the suite's only Application instance.");
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            foreach (var key in new[] { "DefaultButtonStyle", "AccentButtonStyle", "DefaultTextBoxStyle", "DefaultComboBoxStyle", "DefaultTabItemStyle", "PrimaryButton" })
                Require(app.TryFindResource(key) is Style, "Missing Fluent style: " + key);

            var main = new MainWindow(null);
            var picker = new FormPickerWindow(Forms);
            var document = PresetDocument.Create("FOOD", PresetKind.Custom);
            var editor = new ValueEditorWindow(document, Forms, PresetValue.Map(), "CustomRule", "Rule");
            var groups = new GroupPickerWindow([], Forms);
            var yaml = new TextBox { Text = "formsLists: []", IsReadOnly = true, FontFamily = new FontFamily("Consolas") };
            var yamlWindow = new Window { Content = yaml };
            var primary = new Button { Content = "Apply" };
            primary.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            var buttonWindow = new Window { Content = primary };
            var windows = new Window[] { main, picker, editor, groups, yamlWindow, buttonWindow };
            var grid = Field<DataGrid>(picker, "results");
            grid.SelectedIndex = 0;
            var text = Field<TextBox>(picker, "search");
            var combo = Field<ComboBox>(picker, "types");
            var dark = CheckTheme(app, EditorTheme.Dark, windows, [text, yaml, combo, grid]);
            var light = CheckTheme(app, EditorTheme.Light, windows, [text, yaml, combo, grid]);
            if (!SystemParameters.HighContrast)
                Require(dark != light, "Switching theme did not repaint an existing main window.");
            Require(CheckTheme(app, EditorTheme.Dark, windows, [text, yaml, combo, grid]) == dark,
                "Switching back to Dark did not restore the existing windows.");
            CheckSelection(main);
            CheckAccent(app, primary);
            CheckCancel(app);
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            app.Shutdown();
        }
    }

    private static Color CheckTheme(App app, EditorTheme theme, Window[] windows, Control[] controls)
    {
        app.ApplyAppearance(new AppearanceSettings(theme));
        var background = Solid((Brush)app.FindResource("ApplicationBackgroundBrush"));
        foreach (var window in windows)
        {
            window.ApplyTemplate();
            window.Measure(new Size(1400, 900));
            window.Arrange(new Rect(0, 0, 1400, 900));
            window.UpdateLayout();
            Require(Solid(window.Background) == background, window.GetType().Name + " did not follow the application background.");
            Require(Contrast(Solid(window.Foreground), background) >= 4.5, window.GetType().Name + " text lacks contrast.");
        }
        foreach (var control in controls)
        {
            control.ApplyTemplate();
            Require(control.Template is not null, control.GetType().Name + " has no control template.");
            var surface = Blend(Solid(control.Background), background);
            Require(Contrast(Blend(Solid(control.Foreground), surface), surface) >= 4.5,
                control.GetType().Name + " text lacks contrast in " + theme + ".");
        }
        return background;
    }

    private static void CheckSelection(Window window)
    {
        var selectedBackground = (Brush)window.FindResource("DataGridRowSelectedBackgroundThemeBrush");
        var selectedForeground = (Brush)window.FindResource("DataGridRowSelectedForegroundThemeBrush");
        Require(Contrast(Solid(selectedForeground), Solid(selectedBackground)) >= 4.5,
            "Selected form text lacks contrast.");
    }

    private static void CheckAccent(App app, Button primary)
    {
        app.ApplyAppearance(new AppearanceSettings());
        var native = Solid(primary.Background);
        app.ApplyAppearance(new AppearanceSettings(Accent: EditorAccent.Teal));
        var teal = Solid(primary.Background);
        app.ApplyAppearance(new AppearanceSettings(Accent: EditorAccent.Purple));
        if (!SystemParameters.HighContrast)
        {
            Require(teal != Solid(primary.Background), "Changing the button accent did not repaint an existing button.");
            Require(Contrast(Solid(primary.Foreground), Solid(primary.Background)) >= 4.5, "The custom button accent has unreadable text.");
        }
        app.ApplyAppearance(new AppearanceSettings());
        Require(Solid(primary.Background) == native, "Windows accent did not remove the previous button override.");
    }

    private static void CheckCancel(App app)
    {
        var original = new AppearanceSettings(EditorTheme.Dark, EditorAccent.Teal);
        app.ApplyAppearance(original);
        var dialog = new AppearanceWindow(app);
        Field<ComboBox>(dialog, "themes").SelectedIndex = (int)EditorTheme.Light;
        Field<ComboBox>(dialog, "accents").SelectedIndex = (int)EditorAccent.Purple;
        Require(app.Appearance == new AppearanceSettings(EditorTheme.Light, EditorAccent.Purple), "Appearance controls did not preview their values.");
        dialog.Close();
        Require(app.Appearance == original, "Closing Appearance without saving did not undo its preview.");
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static Color Solid(Brush brush) => brush is SolidColorBrush solid
        ? solid.Color : throw new InvalidOperationException("Expected a solid theme brush, got " + brush?.GetType().Name);

    private static Color Blend(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        byte Channel(byte front, byte back) => (byte)Math.Round(front * alpha + back * (1 - alpha));
        return Color.FromRgb(Channel(foreground.R, background.R), Channel(foreground.G, background.G), Channel(foreground.B, background.B));
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
