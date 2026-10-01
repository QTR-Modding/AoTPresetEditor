using System.IO;
using System.Windows;

namespace AoTPresetEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
}
