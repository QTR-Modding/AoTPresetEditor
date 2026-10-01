using System.Windows;
using System.Windows.Controls;

namespace AoTPresetEditor;

public sealed class AppearanceWindow : Window
{
    private readonly App app;
    private readonly AppearanceSettings original;
    private readonly ComboBox themes = new();
    private readonly ComboBox accents = new();
    private readonly TextBlock status = EditorControls.Text("");
    private bool saved;

    public AppearanceWindow(App app)
    {
        this.app = app;
        original = app.Appearance;
        Title = "Appearance";
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(EditorControls.Text("Appearance", true));
        panel.Children.Add(EditorControls.Text("Theme"));
        themes.ItemsSource = new[] { "Dark", "Light", "Follow Windows" };
        themes.SelectedIndex = (int)original.Theme;
        themes.Margin = new Thickness(0, 0, 0, 16);
        panel.Children.Add(themes);
        panel.Children.Add(EditorControls.Text("Button accent"));
        accents.ItemsSource = new[] { "Windows accent", "Blue", "Teal", "Purple", "Amber" };
        accents.SelectedIndex = (int)original.Accent;
        accents.Margin = new Thickness(0, 0, 0, 16);
        panel.Children.Add(accents);
        panel.Children.Add(EditorControls.Text("Changes preview immediately. Save keeps your choices for the next launch."));
        status.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        panel.Children.Add(status);
        var save = EditorControls.Button("Save", Save);
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        EditorControls.Footer(panel, save, EditorControls.Button("Cancel", Close));
        Content = panel;
        themes.SelectionChanged += (_, _) => Preview();
        accents.SelectionChanged += (_, _) => Preview();
        Closed += (_, _) => { if (!saved) app.ApplyAppearance(original); };
    }

    private AppearanceSettings Selection => new((EditorTheme)themes.SelectedIndex, (EditorAccent)accents.SelectedIndex);

    private void Preview()
    {
        if (themes.SelectedIndex >= 0 && accents.SelectedIndex >= 0) app.ApplyAppearance(Selection);
    }

    private void Save()
    {
        try
        {
            Selection.Save(AppearanceSettings.DefaultPath);
            app.ApplyAppearance(Selection);
            saved = true;
            Close();
        }
        catch (Exception error) { status.Text = "Could not save appearance: " + error.Message; }
    }
}
