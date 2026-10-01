using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace AoTPresetEditor;

public partial class App : Application
{
    public AppearanceSettings Appearance { get; private set; } = new();
    private static readonly string[] AccentButtonKeys =
    [
        "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
        "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed"
    ];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyAppearance(AppearanceSettings.Load(AppearanceSettings.DefaultPath));
        SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        string? dataFolder = null;
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--data" && i + 1 < e.Args.Length)
                dataFolder = Path.GetFullPath(e.Args[++i]);
            else
            {
                MessageBox.Show("Usage: AoTPresetEditor.exe --data \"path to Skyrim Data\"", "AoT Preset Editor");
                Shutdown(1);
                return;
            }
        }
        new MainWindow(dataFolder).Show();
    }

    public void ApplyAppearance(AppearanceSettings settings)
    {
        Appearance = settings;
#pragma warning disable WPF0001 // Use the Fluent theme supplied by this app's bundled WPF runtime.
        ThemeMode = settings.Theme switch
        {
            EditorTheme.Light => System.Windows.ThemeMode.Light,
            EditorTheme.System => System.Windows.ThemeMode.System,
            _ => System.Windows.ThemeMode.Dark
        };
#pragma warning restore WPF0001
        ApplyButtonAccent();
    }

    private void ApplyButtonAccent()
    {
        foreach (var key in AccentButtonKeys) Resources.Remove(key);
        if (SystemParameters.HighContrast || Appearance.Accent == EditorAccent.System) return;
        var colors = Appearance.Accent switch
        {
            EditorAccent.Blue => new[] { "#315FB0", "#2A5196", "#23427B" },
            EditorAccent.Teal => new[] { "#006D77", "#005D65", "#004C53" },
            EditorAccent.Purple => new[] { "#6B45B2", "#5B3B97", "#4B307D" },
            EditorAccent.Amber => new[] { "#9A5C00", "#834E00", "#6C4000" },
            _ => []
        };
        for (var index = 0; index < colors.Length; index++)
            Resources[AccentButtonKeys[index]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[index]));
        if (colors.Length == 0) return;
        foreach (var key in AccentButtonKeys.Skip(colors.Length)) Resources[key] = Brushes.White;
    }

    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.Invoke(ApplyButtonAccent);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        base.OnExit(e);
    }
}
