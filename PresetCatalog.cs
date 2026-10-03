using System.Diagnostics;
using System.IO;
using System.Text;

namespace AoTPresetEditor;

public enum PresetKind { Default, Custom, Addon, Other }

public sealed record PresetFile(string FullPath, string RelativePath)
{
    public string DisplayName => Path.GetFileNameWithoutExtension(FullPath) + " · " + Category;
    public string Category
    {
        get
        {
            var separator = RelativePath.IndexOf(Path.DirectorySeparatorChar);
            return separator < 0 ? "Root" : RelativePath[..separator];
        }
    }

    public string CategoryPath => RelativePath[(RelativePath.IndexOf(Path.DirectorySeparatorChar) + 1)..];

    public PresetKind Kind
    {
        get
        {
            if (CategoryPath.Equals($"AoT_default{Category}.yml", StringComparison.OrdinalIgnoreCase))
                return PresetKind.Default;
            var separator = CategoryPath.IndexOf(Path.DirectorySeparatorChar);
            if (separator < 0) return PresetKind.Other;
            return CategoryPath[..separator].ToLowerInvariant() switch
            {
                "custom" => PresetKind.Custom,
                "addon" => PresetKind.Addon,
                _ => PresetKind.Other
            };
        }
    }

    public string RulePath => Kind is PresetKind.Custom or PresetKind.Addon
        ? CategoryPath[(CategoryPath.IndexOf(Path.DirectorySeparatorChar) + 1)..]
        : CategoryPath;
}

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
