using System.Diagnostics;
using System.IO;
using System.Text;

namespace AoTPresetEditor;

public sealed record PresetFile(string FullPath, string RelativePath);

public static class PresetCatalog
{
    public static IReadOnlyList<PresetFile> Discover(string dataFolder)
    {
        var root = Path.Combine(dataFolder, "SKSE", "Plugins", "AlchemyOfTime");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.yml", SearchOption.AllDirectories)
            .Select(path => new PresetFile(path, Path.GetRelativePath(root, path)))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task<string> Expand(string yaml, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "AoTYaml.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var process = Process.Start(start) ?? throw new IOException("Could not start YAML expansion.");
        using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        IOException? inputError = null;
        try { await process.StandardInput.WriteAsync(yaml.AsMemory(), cancellationToken); }
        catch (IOException exception) { inputError = exception; }
        try { process.StandardInput.Close(); }
        catch (IOException exception) { inputError ??= exception; }
        await process.WaitForExitAsync(cancellationToken);
        var diagnostics = await error;
        var expanded = await output;
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidDataException(diagnostics);
        if (inputError is not null) throw inputError;
        return expanded;
    }
}
