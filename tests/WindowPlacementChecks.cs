using System.IO;
using System.Windows;
using AoTPresetEditor;

internal static class WindowPlacementChecks
{
    public static void Run(App app)
    {
        var folder = Path.Combine(Path.GetTempPath(), "AoT-window-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "window.json");
        Directory.CreateDirectory(folder);
        var originalAppearance = app.Appearance;
        try
        {
            Require(WindowPlacement.Load(path) is null, "A first launch must use the default window layout.");
            foreach (var invalid in new[] { "broken", "null", "{}", "{\"Left\":10,\"Right\":5,\"Bottom\":100}" })
            {
                File.WriteAllText(path, invalid);
                Require(WindowPlacement.Load(path) is null, "Invalid saved placement must fall back to the default layout.");
            }
            File.Delete(path);
            var unopened = new MainWindow(null, path);
            unopened.Close();
            Require(!File.Exists(path), "Closing a window without an initialized handle must not save meaningless bounds.");

            var initial = Create(path);
            initial.Width = 600; initial.Height = 400; initial.Left = 80; initial.Top = 80;
            var normal = WindowPlacement.Capture(initial, false)
                ?? throw new InvalidOperationException("The initialized window must expose real native placement.");
            initial.Close();
            Require(WindowPlacement.Load(path) == normal, "Closing must persist the actual normal window rectangle.");

            app.ApplyAppearance(originalAppearance with { Scale = 75 });
            var reopened = Create(path);
            Require(WindowPlacement.Capture(reopened, false) == normal,
                "Reopening must restore the saved native bounds independently of interface scale.");
            reopened.WindowState = WindowState.Maximized;
            reopened.WindowState = WindowState.Minimized;
            reopened.Close();
            var maximized = WindowPlacement.Load(path);
            Require(maximized == normal with { Maximized = true },
                $"Closing a minimized maximized window must retain its normal bounds and maximized state: expected {normal with { Maximized = true }}, got {maximized}.");

            var restored = Create(path);
            Require(restored.WindowState == WindowState.Maximized, "The saved maximized state must be restored on reopening.");
            restored.WindowState = WindowState.Normal;
            Require(WindowPlacement.Capture(restored, false) == normal,
                "Restoring from maximized must return to the previous normal size.");
            restored.WindowState = WindowState.Minimized;
            restored.Close();
            var normalAgain = Create(path);
            Require(normalAgain.WindowState == WindowState.Normal,
                "A window closed while minimized must reopen visibly in its last non-minimized state.");
            normalAgain.Close();
        }
        finally
        {
            foreach (var window in app.Windows.OfType<MainWindow>().ToArray()) window.Close();
            app.ApplyAppearance(originalAppearance);
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    private static MainWindow Create(string path)
    {
        var window = new MainWindow(null, path)
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false
        };
        window.Show();
        return window;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
