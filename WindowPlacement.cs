using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace AoTPresetEditor;

public sealed record WindowPlacement(int Left, int Top, int Right, int Bottom, bool Maximized)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AoTPresetEditor", "window.json");

    private bool HasBounds => Right > Left && Bottom > Top;

    public static WindowPlacement? Load(string? path = null)
    {
        try
        {
            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path ?? DefaultPath));
            return placement is { HasBounds: true } ? placement : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(string? path = null)
    {
        var destination = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, destination, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static WindowPlacement? Capture(Window window, bool maximized)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var placement = new NativePlacement { Length = (uint)Marshal.SizeOf<NativePlacement>() };
        if (handle == nint.Zero || !GetWindowPlacement(handle, ref placement)) return null;
        var bounds = placement.NormalPosition;
        var saved = new WindowPlacement(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, maximized);
        return saved.HasBounds ? saved : null;
    }

    public bool Restore(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!HasBounds || handle == nint.Zero) return false;
        var placement = new NativePlacement
        {
            Length = (uint)Marshal.SizeOf<NativePlacement>(),
            ShowCommand = Maximized ? ShowCommand.Maximized : ShowCommand.Normal,
            NormalPosition = new NativeRect { Left = Left, Top = Top, Right = Right, Bottom = Bottom }
        };
        return SetWindowPlacement(handle, in placement);
    }

    private enum ShowCommand : uint { Normal = 1, Maximized = 3 }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        public uint Length, Flags;
        public ShowCommand ShowCommand;
        public NativePoint MinPosition, MaxPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window, ref NativePlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint window, in NativePlacement placement);
}
